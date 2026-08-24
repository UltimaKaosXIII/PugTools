using System;
using System.Collections.Generic;
using SlimDX;

namespace FileFormats {
  public sealed class DynamicDetailPlacement {
    public Vector3 Position;
    public float Scale;
    public Vector3 Tint;
    public float AtlasPacked;
    public float Flip;
    public float PackedNormal;
    public Vector3 SurfaceNormal;
    public Vector3 Rotation;
  }

  /// <summary>
  /// CPU port of Jedipedia's SWTOR DYD scatter recipe. The game stores only a byte density grid on each
  /// heightmap; cards/meshes are deterministically reconstructed from MSVCRT rand(), Perlin variation and
  /// the 15 raw type-0 channel parameters from area.dat.
  /// </summary>
  public static class DynamicDetailScatter {
    private const float GridStep=.2f;
    private const int BillboardStrideBatchCount=5;
    private static readonly object PerlinLock=new object();
    private static Vector3[] perlinGradients;
    private static byte[] perlinPermutation;

    private sealed class MsvcRandom {
      private uint state;
      public MsvcRandom(int seed){state=unchecked((uint)seed);}
      public int Next(){state=unchecked(state*214013u+2531011u);return (int)((state>>16)&0x7fff);}
    }

    private static float Param(uint[] values,int index)=>values!=null&&index>=0&&index<values.Length?values[index]:0;

    public static List<DynamicDetailPlacement> Build(HeightMap hm, DynamicDetailPaint paint, AreaDynamicDetailChannelParam param, bool mesh, int detailDensityPercent=100, int maxPlacements=50000) {
      var result=new List<DynamicDetailPlacement>();
      if(hm==null||paint?.Density==null||param?.Values==null||param.Values.Length<15)return result;
      int width=checked((int)hm.width),depth=checked((int)hm.depth),vertexCount=width*depth;
      if(width<2||depth<2||paint.Density.Length!=vertexCount)return result;
      uint[] values=param.Values;float divisor=mesh?12f:1.5f;
      int candidateCap=1+(int)Math.Floor(Math.Max(0,Param(values,1))*Math.Max(0,detailDensityPercent)/(divisor*100f));
      candidateCap=Math.Min(candidateCap,4096); // corrupt params must not explode a tile.
      float frequency=Param(values,2)/25f;
      float xmin=-((float)Math.Floor(width/2f))*GridStep,zmin=-((float)Math.Floor(depth/2f))*GridStep;
      Vector3[] normals=BuildCornerNormals(hm,width,depth);
      int blockColumns=(int)Math.Ceiling((width-1)/4f);
      byte[] cursors=mesh?null:new byte[blockColumns*(int)Math.Ceiling((depth-1)/4f)];
      MsvcRandom lastRandom=null;

      for(int z=0;z<depth-1&&result.Count<maxPlacements;z++)for(int x=0;x<width-1&&result.Count<maxPlacements;x++){
        if(hm.hasHoles&&!hm.CheckNoHole(x,z))continue;
        int d00=paint.Density[z*width+x],d10=paint.Density[z*width+x+1],d01=paint.Density[(z+1)*width+x],d11=paint.Density[(z+1)*width+x+1];
        if((d00|d10|d01|d11)==0)continue;
        int strength=(d00+d10+d01+d11)/4;
        var random=new MsvcRandom(unchecked(x*12263+z*22487-paint.ChannelId));lastRandom=random;
        uint randomWord=0;float? cellNoise=null;float cellScale=0;Vector3 cellTint=Vector3.Zero;Vector3 surfaceNormal=normals[z*width+x];
        int block=mesh?0:(z/4)*blockColumns+(x/4);
        for(int candidate=0;candidate<candidateCap&&result.Count<maxPlacements;candidate++){
          float fx=random.Next()/32768f,fz=random.Next()/32768f;
          if(randomWord==0)randomWord=(uint)random.Next();int threshold=(int)(randomWord&0xff);randomWord>>=8;
          if(threshold>=strength)continue;
          float px=xmin+x*GridStep+fx*GridStep,pz=zmin+z*GridStep+fz*GridStep;
          float py=SurfaceHeight(hm,width,x,z,fx,fz);
          var placement=new DynamicDetailPlacement{Position=new Vector3(px,py,pz),SurfaceNormal=surfaceNormal,Tint=new Vector3(1,1,1)};
          if(!mesh){
            int creationBatch=cursors[block];cursors[block]=(byte)(creationBatch+1<BillboardStrideBatchCount?creationBatch+1:0);
            if(!cellNoise.HasValue){cellNoise=ThreeOctaveNoise(frequency*px,frequency*pz);cellScale=BillboardScale(values,strength,cellNoise.Value);cellTint=BillboardTint(values,cellNoise.Value);}
            placement.Position.Y=py+.9f*cellScale;placement.Scale=cellScale;placement.Tint=cellTint;placement.AtlasPacked=creationBatch*4;placement.PackedNormal=PackNormal(surfaceNormal);
          } else {
            float noise=ThreeOctaveNoise(frequency*px,frequency*pz);placement.Scale=MeshScale(values,strength,noise);
            var instanceRandom=new MsvcRandom(unchecked((int)(px*12263+pz*22487-paint.ChannelId)));
            placement.Rotation=new Vector3(instanceRandom.Next()/32768f*(float)Math.PI*2,instanceRandom.Next()/32768f*(float)Math.PI*2,instanceRandom.Next()/32768f*(float)Math.PI*2);
          }
          result.Add(placement);
        }
      }

      if(!mesh&&result.Count>0){
        var random=lastRandom??new MsvcRandom(-paint.ChannelId);uint word=0;
        int NextByte(){if(word==0)word=(uint)random.Next();int b=(int)(word&0xff);word>>=8;return b;}
        int atlas=(int)Param(values,0);
        foreach(var p in result){if(atlas==1)p.AtlasPacked+=NextByte()&3;else if(atlas==2)p.AtlasPacked+=NextByte()&1;p.Flip=NextByte()<0x80?1:0;}
      }
      return result;
    }

    private static float SurfaceHeight(HeightMap hm,int width,int x,int z,float fx,float fz){
      float h00=hm.elevation[z,x],h10=hm.elevation[z,x+1],h01=hm.elevation[z+1,x],h11=hm.elevation[z+1,x+1];
      if(fx+fz<=1)return h00+(h10-h00)*fx+(h01-h00)*fz;
      return h11+(h01-h11)*(1-fx)+(h10-h11)*(1-fz);
    }

    private static Vector3[] BuildCornerNormals(HeightMap hm,int width,int depth){
      var sum=new Vector3[width*depth];
      for(int z=0;z<depth-1;z++)for(int x=0;x<width-1;x++){
        if(hm.hasHoles&&!hm.CheckNoHole(x,z))continue;
        int a=z*width+x,b=(z+1)*width+x,c=z*width+x+1,d=(z+1)*width+x+1;
        Vector3 pa=new Vector3(0,hm.elevation[z,x],0),pb=new Vector3(0,hm.elevation[z+1,x],GridStep),pc=new Vector3(GridStep,hm.elevation[z,x+1],0),pd=new Vector3(GridStep,hm.elevation[z+1,x+1],GridStep);
        Vector3 n1=UnitNormal(pa,pb,pc),n2=UnitNormal(pc,pb,pd),shared=Normalize(n1+n2);
        sum[a]+=n1;sum[b]+=shared;sum[c]+=shared;sum[d]+=n2;
      }
      for(int i=0;i<sum.Length;i++)sum[i]=Normalize(sum[i]);return sum;
    }
    private static Vector3 UnitNormal(Vector3 a,Vector3 b,Vector3 c)=>Normalize(Vector3.Cross(b-a,c-a));
    private static Vector3 Normalize(Vector3 v){if(v.LengthSquared()<.000001f)return new Vector3(0,1,0);v.Normalize();return v;}

    private static float BillboardScale(uint[] v,int paint,float noise){float b=(Param(v,10)+32)/2400f,s=b,strength=Param(v,12);if(strength>0)s-= (1-paint/255f)*(strength/100f)*b;float variation=Param(v,11);if(variation>0)s+=(variation/100f)*(noise/15f);return Math.Max(.0001f,s);}
    private static float MeshScale(uint[] v,int paint,float noise){float b=Param(v,10)/25.64f+.1f,s=b,strength=Param(v,12);if(strength>0)s-=(1-paint/255f)*(strength/100f)*b;float variation=Param(v,11);if(variation>0)s+=(variation/100f)*noise*1.5f;return Math.Max(.01f,s);}
    private static Vector3 BillboardTint(uint[] v,float noise){float h=WrapHue((Param(v,3)+Param(v,4)*noise)/100f),s=(Param(v,5)+Param(v,6)*noise)/100f,l=(Param(v,7)+Param(v,8)*noise)/100f;return Hsl(h,s,l);}
    private static float WrapHue(float h){if(h>=1)h-=1;else if(h<0)h+=1;return h;}
    private static Vector3 Hsl(float h,float s,float l){if(s==0)return Clamp(new Vector3(l,l,l));float q=l<=.5f?l*(1+s):l+s-l*s,p=2*l-q;return Clamp(new Vector3(Hue(p,q,h+1f/3f),Hue(p,q,h),Hue(p,q,h-1f/3f)));}
    private static float Hue(float p,float q,float t){if(t<0)t+=1;else if(t>1)t-=1;if(t<1f/6f)return p+(q-p)*6*t;if(t<.5f)return q;if(t<2f/3f)return p+(q-p)*6*(2f/3f-t);return p;}
    private static Vector3 Clamp(Vector3 v)=>new Vector3(Math.Max(0,Math.Min(1,v.X)),Math.Max(0,Math.Min(1,v.Y)),Math.Max(0,Math.Min(1,v.Z)));
    private static float PackNormal(Vector3 n){int x=PackComponent(n.X),y=PackComponent(n.Y),z=PackComponent(n.Z);return x|(y<<8)|(z<<16);}
    private static int PackComponent(float value)=>Math.Max(0,Math.Min(255,(int)((value*.5f+.5f)*255f)));

    private static float ThreeOctaveNoise(float x,float z){float amp=1,result=0,px=x,pz=z;for(int o=0;o<3;o++){result+=Perlin(px,0,pz)/amp;px*=2;pz*=2;amp*=2;}return result;}
    private static float Fade(float t)=>((t*6-15)*t+10)*t*t*t;
    private static float Lerp(float a,float b,float t)=>a+(b-a)*t;
    private static float Perlin(float x,float y,float z){EnsurePerlin();int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y),iz=(int)Math.Floor(z);float fx=x-ix,fy=y-iy,fz=z-iz;
      float Grad(int ox,int oy,int oz){int hash=perlinPermutation[(perlinPermutation[(perlinPermutation[(ix+ox)&255]+iy+oy)&255]+iz+oz)&255];Vector3 g=perlinGradients[hash];float dx=fx-ox,dy=fy-oy,dz=fz-oz;/* native decompile uses one delta sum for all gradient lanes */return g.X*(dx+dy+dz);}
      float u=Fade(fx),v=Fade(fy),w=Fade(fz);float x00=Lerp(Grad(0,0,0),Grad(1,0,0),u),x10=Lerp(Grad(0,1,0),Grad(1,1,0),u),x01=Lerp(Grad(0,0,1),Grad(1,0,1),u),x11=Lerp(Grad(0,1,1),Grad(1,1,1),u);return Lerp(Lerp(x00,x10,v),Lerp(x01,x11,v),w);
    }
    private static void EnsurePerlin(){if(perlinPermutation!=null)return;lock(PerlinLock){if(perlinPermutation!=null)return;var r=new MsvcRandom(unchecked((int)0x84525396));var g=new Vector3[256];for(int i=0;i<256;i++){Vector3 v;do{v=new Vector3(r.Next()/32767f*2-1,r.Next()/32767f*2-1,r.Next()/32767f*2-1);}while(v.LengthSquared()==0||v.LengthSquared()>1);v.Normalize();g[i]=v;}var p=new byte[256];for(int i=0;i<256;i++)p[i]=(byte)i;for(int i=0;i<2560;i++){int a=i&255,b=(int)((r.Next()/32767f)*255.49001f);byte t=p[a];p[a]=p[b];p[b]=t;}perlinGradients=g;perlinPermutation=p;}}
  }
}
