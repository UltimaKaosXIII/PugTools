cbuffer WorldPerFrame {
    float4x4 ViewProj;
    float4 CameraPosition;
    float4 PointLightDir;
    float4 FrontLightColor;
    float4 AmbientLightColor;
    float4 AdvancedLightingParams;
    float4 FogColor0;
    float4 FogColor1;
    float4 FogColorSky;
    float4 FogParams;
    float4 FogColorParams;
    float4 ShadowSplits;
    float4 LocalLightPosRange[4];
    float4 LocalLightColorIntensity[4];
    float4 LocalLightDirType[4];
    float4x4 LocalLightProjectorInv[4];
    // x=SourceOffset, y=has illumination cookie, z=has falloff cookie, w=has authored ramp.
    float4 LocalLightProjectorParams[4];
    float4 HeightRange;
    float4 OverlayColor;
    float4 MaterialFlatColor;
    float4 MaterialBloomParams;
    // x=min distance, y=min opacity, z=max distance, w=max opacity.
    float4 OpacityFadeParams;
    float4 ScrollingOffsetParams;
    float4 ScrollingVisualParams;
    float4 EnvBlendParams1;
    float4 EnvBlendParams2;
    float2 MaterialUvScale;
    float2 TerrainChunkScale;
    int EnableLighting;
    int EnableFog;
    int EnableShadows;
    int LocalLightCount;
    int AlphaMode;
    int MaterialUsesEmissive;
    int MaterialIsOpacityFade;
    float HookOpacity;
    float AlphaTestValue;
    float DiffuseTextureAlphaMax;
    float TerrainUvScale;
    int HasDiffuse;
    int HasDiffuse2;
    int HasRotation;
    int HasGloss;
    int HasTerrainMask;
    int HasTerrainColor;
    int HasIllumination;
    int ScrollingEnabled;
    int HasEnvBlend;
    int EnvBlendEnableCreviceMap;
    float EnvBlendMaterialTileMult;
    float TimeSeconds;
    int HasColorLut;
    float MapArtOpacity;
    float ViewerBlueGlow;
    float4 MapArtTint;
    float4x4 TaaReprojection;
    // xy = inverse render-target size, zw = current sub-pixel jitter in pixel/texture coordinates.
    float4 TaaParams0;
    // x = history weight, y = whether a valid history sample exists.
    float4 TaaParams1;
    float4 DydCameraRight;
    float4 DydCreationDistances;
    float4 DydParams;
    float4 DydTextureInfo;
    float4 WaterDeepColor;
    float4 WaterShallowColor;
    float4 WaterGlossColor;
    float4 WaterNmOffsets;
    float4 WaterParams;
    float4 WaterParams2;
    float4 WaterParams3;
    float4 WaterAlphaParams1;
    float4 WaterAlphaParams2;
    float4 WaterAlphaParams3;
    float4 WaterAlphaParams4;
    int HasWaterNormal1;
    int HasWaterNormal2;
    int HasWaterDepth;
    int HasWaterSurface;
    int HasWaterEnvironment;
    float3 WaterPadding0;
    // Jedipedia-style NPC nameplate visibility pass. Each entry stores scene-depth UV in xy and
    // the projected window depth in z; the GPU writes one visibility texel per label.
    float4 NameplateSamples[192];
    // xy = main viewport dimensions, zw = inverse main viewport dimensions.
    float4 NameplateVisibilityParams;
    float NameplateDepthBias;
    float3 NameplatePadding0;
    // Conservative per-object visibility probes. xy = scene-depth UV, z = the nearest depth of the receiver's
    // world bounding sphere, w = projected radius in pixels. The result is consumed on the following frame.
    float4 ObjectVisibilitySamples[512];
    // x = output width, y/z = main viewport width/height, w = depth bias.
    float4 ObjectVisibilityParams;
};

cbuffer WorldPerObject {
    float4x4 World;
    float4x4 WorldInvTranspose;
    float4x4 ShadowMatrix0;
    float4x4 ShadowMatrix1;
    float4x4 ShadowMatrix2;
    float4x4 ShadowMatrix3;
};

// Population meshes use the same 256-bone palette path as PugTools' dedicated GR2 viewer. Keeping skinning on
// the GPU avoids remapping/uploading every NPC vertex every frame while preserving the exact JBA pose matrices.
cbuffer WorldSkinning {
    float4x4 SkinPalette[256];
};

Texture2D DiffuseMap;
Texture2D DiffuseMap2;
Texture2D RotationMap;
Texture2D GlossMap;
Texture2D TerrainMask;
Texture2D TerrainColorMap;
Texture2D IlluminationMap;
Texture2D ScrollingTextureMap;
Texture2D ScrollingMaskMap;
Texture2D EnvBlendDiffuseMap;
Texture2D EnvBlendNormalMap;
Texture2D ShadowMap0;
Texture2D ShadowMap1;
Texture2D ShadowMap2;
Texture2D ShadowMap3;
Texture2D SceneColorMap;
Texture2D HistoryColorMap;
Texture2D SceneDepthMap;
Texture2D MapArtMap;
Texture3D ColorLut;
Texture2D WaterNormalMap1;
Texture2D WaterNormalMap2;
Texture2D WaterDepthMap;
Texture3D WaterSurfaceMap;
TextureCube WaterEnvironmentMap;
// FX11/SM5.0 does not support dynamically indexing arrays of texture objects.
// Keep the four local-light textures as separate resources and select them in helper functions.
Texture2D LocalLightIlluminationMap0;
Texture2D LocalLightIlluminationMap1;
Texture2D LocalLightIlluminationMap2;
Texture2D LocalLightIlluminationMap3;
Texture2D LocalLightFalloffMap0;
Texture2D LocalLightFalloffMap1;
Texture2D LocalLightFalloffMap2;
Texture2D LocalLightFalloffMap3;
Texture2D LocalLightRampMap0;
Texture2D LocalLightRampMap1;
Texture2D LocalLightRampMap2;
Texture2D LocalLightRampMap3;

SamplerState LinearWrap { Filter = MIN_MAG_MIP_LINEAR; AddressU = Wrap; AddressV = Wrap; };
SamplerState LinearClamp { Filter = MIN_MAG_MIP_LINEAR; AddressU = Clamp; AddressV = Clamp; };
SamplerState PointClamp { Filter = MIN_MAG_MIP_POINT; AddressU = Clamp; AddressV = Clamp; };
SamplerComparisonState ShadowSampler { Filter = COMPARISON_MIN_MAG_LINEAR_MIP_POINT; AddressU = Border; AddressV = Border; ComparisonFunc = Less_Equal; BorderColor = float4(1,1,1,1); };

RasterizerState SolidRS { FillMode = Solid; CullMode = None; DepthClipEnable = TRUE; };
// Jedipedia feeds authored LOD -3 / OCCLUDER_ONLY geometry into dPVS rather than drawing it. PugTools' D3D11
// fallback uses the same geometry as a depth-only prepass. A small positive bias pushes the simplified occluder
// behind coincident visible walls, avoiding holes while still rejecting geometry clearly hidden behind them.
RasterizerState OccluderRS { FillMode = Solid; CullMode = None; DepthClipEnable = TRUE; DepthBias = 4; SlopeScaledDepthBias = 0.5; };
// WTR surfaces are authored one-sided. Jedipedia/WebGL culls back faces; rendering the underside in
// PugTools turns a swamp plane into the huge grey/white horizontal slab seen while flying below it.
RasterizerState WaterRS { FillMode = Solid; CullMode = Back; FrontCounterClockwise = TRUE; DepthClipEnable = TRUE; };
RasterizerState WireRS { FillMode = Wireframe; CullMode = None; DepthClipEnable = TRUE; };
DepthStencilState DepthWriteDSS { DepthEnable = TRUE; DepthWriteMask = ALL; DepthFunc = LESS_EQUAL; };
DepthStencilState DepthReadDSS { DepthEnable = TRUE; DepthWriteMask = ZERO; DepthFunc = LESS_EQUAL; };
DepthStencilState NoDepthDSS { DepthEnable = FALSE; DepthWriteMask = ZERO; };
BlendState OpaqueBS { BlendEnable[0] = FALSE; RenderTargetWriteMask[0] = 0x0F; };
BlendState DepthOnlyBS { BlendEnable[0] = FALSE; RenderTargetWriteMask[0] = 0x00; };
BlendState AlphaBS {
    BlendEnable[0] = TRUE;
    SrcBlend[0] = SRC_ALPHA;
    DestBlend[0] = INV_SRC_ALPHA;
    BlendOp[0] = ADD;
    SrcBlendAlpha[0] = ONE;
    DestBlendAlpha[0] = INV_SRC_ALPHA;
    BlendOpAlpha[0] = ADD;
    RenderTargetWriteMask[0] = 0x0F;
};
BlendState AddBS {
    BlendEnable[0] = TRUE;
    SrcBlend[0] = ONE;
    DestBlend[0] = ONE;
    BlendOp[0] = ADD;
    SrcBlendAlpha[0] = ZERO;
    DestBlendAlpha[0] = ONE;
    BlendOpAlpha[0] = ADD;
    RenderTargetWriteMask[0] = 0x0F;
};
BlendState MultiplyBS {
    BlendEnable[0] = TRUE;
    SrcBlend[0] = ZERO;
    DestBlend[0] = SRC_COLOR;
    BlendOp[0] = ADD;
    SrcBlendAlpha[0] = ZERO;
    DestBlendAlpha[0] = ONE;
    BlendOpAlpha[0] = ADD;
    RenderTargetWriteMask[0] = 0x0F;
};

struct VSIn { float3 Pos:POSITION; float3 Normal:NORMAL; float2 Tex:TEXCOORD; float3 Tan:TANGENT; };
struct SkinnedVSIn {
    float3 Pos:POSITION; float3 Normal:NORMAL; float2 Tex:TEXCOORD; float3 Tan:TANGENT;
    float4 Weights:BLENDWEIGHT; uint4 Indices:BLENDINDICES;
};
struct VSOut {
    float4 Pos:SV_POSITION;
    float3 WorldPos:TEXCOORD0;
    float3 Normal:TEXCOORD1;
    float3 Tangent:TEXCOORD2;
    float2 Tex:TEXCOORD3;
    float ViewDistance:TEXCOORD4;
};

VSOut WorldVS(VSIn v) {
    VSOut o;
    float4 wp = mul(float4(v.Pos,1), World);
    o.WorldPos = wp.xyz;
    o.Pos = mul(wp, ViewProj);
    o.Normal = normalize(mul(float4(v.Normal,0), WorldInvTranspose).xyz);
    o.Tangent = normalize(mul(float4(v.Tan,0), World).xyz);
    o.Tex = v.Tex;
    o.ViewDistance = distance(CameraPosition.xyz, wp.xyz);
    return o;
}

VSOut SkinnedWorldVS(SkinnedVSIn v) {
    VSOut o;
    float4 localPos=float4(v.Pos,1);
    float3 localNormal=v.Normal;
    float3 localTangent=v.Tan;
    float weightSum=dot(v.Weights,float4(1.0f,1.0f,1.0f,1.0f));
    if(weightSum>0.00001f) {
        float4 w=v.Weights/weightSum;
        localPos=
            mul(float4(v.Pos,1),SkinPalette[v.Indices.x])*w.x +
            mul(float4(v.Pos,1),SkinPalette[v.Indices.y])*w.y +
            mul(float4(v.Pos,1),SkinPalette[v.Indices.z])*w.z +
            mul(float4(v.Pos,1),SkinPalette[v.Indices.w])*w.w;
        localNormal=
            mul(v.Normal,(float3x3)SkinPalette[v.Indices.x])*w.x +
            mul(v.Normal,(float3x3)SkinPalette[v.Indices.y])*w.y +
            mul(v.Normal,(float3x3)SkinPalette[v.Indices.z])*w.z +
            mul(v.Normal,(float3x3)SkinPalette[v.Indices.w])*w.w;
        localTangent=
            mul(v.Tan,(float3x3)SkinPalette[v.Indices.x])*w.x +
            mul(v.Tan,(float3x3)SkinPalette[v.Indices.y])*w.y +
            mul(v.Tan,(float3x3)SkinPalette[v.Indices.z])*w.z +
            mul(v.Tan,(float3x3)SkinPalette[v.Indices.w])*w.w;
    }
    float4 wp=mul(localPos,World);
    o.WorldPos=wp.xyz; o.Pos=mul(wp,ViewProj);
    o.Normal=normalize(mul(float4(normalize(localNormal),0),WorldInvTranspose).xyz);
    o.Tangent=normalize(mul(float4(normalize(localTangent),0),World).xyz);
    o.Tex=v.Tex; o.ViewDistance=distance(CameraPosition.xyz,wp.xyz);
    return o;
}

struct InstancedVSIn {
    float3 Pos:POSITION; float3 Normal:NORMAL; float2 Tex:TEXCOORD; float3 Tan:TANGENT;
    float4 IWorld0:WORLD0; float4 IWorld1:WORLD1; float4 IWorld2:WORLD2; float4 IWorld3:WORLD3;
};

VSOut InstancedWorldVS(InstancedVSIn v) {
    VSOut o;
    float4x4 instanceWorld=float4x4(v.IWorld0,v.IWorld1,v.IWorld2,v.IWorld3);
    float4 localWp=mul(float4(v.Pos,1),instanceWorld);
    float4 wp=mul(localWp,World);
    o.WorldPos=wp.xyz; o.Pos=mul(wp,ViewProj);
    // DYD mesh placements only contain rotation + uniform scale, so the normalized upper 3x3 is
    // sufficient here and avoids shipping one inverse-transpose matrix per instance.
    float3 instanceNormal=normalize(mul(float4(v.Normal,0),instanceWorld).xyz);
    float3 instanceTangent=normalize(mul(float4(v.Tan,0),instanceWorld).xyz);
    o.Normal=normalize(mul(float4(instanceNormal,0),WorldInvTranspose).xyz);
    o.Tangent=normalize(mul(float4(instanceTangent,0),World).xyz);
    o.Tex=v.Tex; o.ViewDistance=distance(CameraPosition.xyz,wp.xyz);
    return o;
}


struct DydIn {
    float2 Corner:CORNER;
    float3 Center:CENTER;
    float Scale:SCALE;
    float3 Tint:TINT;
    float2 Atlas:ATLAS;
    float PackedNormal:PACKEDNORMAL;
};
struct DydOut {
    float4 Pos:SV_POSITION;
    float3 WorldPos:TEXCOORD0;
    float3 Normal:TEXCOORD1;
    float2 Tex:TEXCOORD2;
    float ViewDistance:TEXCOORD3;
    float3 Tint:TEXCOORD4;
    float Ndl:TEXCOORD5;
    float Backlight:TEXCOORD6;
    float DistanceFade:TEXCOORD7;
};
float3 UnpackGrassNormal(float value) {
    float packed=floor(value+0.5);
    float3 bytes=float3(fmod(packed,256.0),fmod(floor(packed/256.0),256.0),fmod(floor(packed/65536.0),256.0));
    return bytes/127.5-1.0;
}
DydOut DynamicDetailVS(DydIn v) {
    DydOut o;
    float2 cardCorner=v.Corner/0.9;
    float2 unitCorner=cardCorner*0.5+0.5;
    float atlasMode=DydParams.z;
    float columns=atlasMode==0?1.0:2.0;
    float rows=atlasMode==1?2.0:1.0;
    float frameCount=columns*rows;
    float packedFrameBatch=floor(v.Atlas.x+0.5);
    float frame=fmod(packedFrameBatch,frameCount);
    int creationBatch=(int)floor(packedFrameBatch/4.0);
    float2 frameIndex=float2(fmod(frame,columns),floor(frame/columns));
    float2 frameSize=float2(1.0/columns,1.0/rows);
    float2 texSize=max(DydTextureInfo.xy,float2(1,1));
    float2 inset=min(frameSize*0.49,0.5/texSize);
    float localU=v.Atlas.y>0.5?1.0-unitCorner.x:unitCorner.x;
    float2 frameUv=float2(localU,1.0-unitCorner.y);
    o.Tex=frameIndex*frameSize+inset+frameUv*(frameSize-inset*2.0);

    float3 center=mul(float4(v.Center,1),World).xyz;
    float horizontalDistance=length(CameraPosition.xz-center.xz);
    float creationDistance=creationBatch<=0?DydCreationDistances.x:(creationBatch==1?DydCreationDistances.y:(creationBatch==2?DydCreationDistances.z:(creationBatch==3?DydCreationDistances.w:DydParams.x)));
    float distanceFade=1.0-saturate(horizontalDistance-(creationDistance-1.6));
    float3 cameraRight=normalize(DydCameraRight.xyz);
    float aspect=(texSize.x*rows)/max(1.0,texSize.y*columns);
    float windPhase=v.Center.x*32.0+TimeSeconds;
    float2 wind=float2(cos(windPhase),sin(windPhase))*(DydParams.y*unitCorner.y*0.0001225*distanceFade);
    float verticalOffset=v.Scale*(cardCorner.y*distanceFade-0.9*(1.0-distanceFade));
    o.WorldPos=center+cameraRight*(cardCorner.x*v.Scale*aspect+wind.x)+float3(0,verticalOffset+wind.y,0);
    o.Pos=mul(float4(o.WorldPos,1),ViewProj);
    if(distanceFade<0.001)o.Pos=float4(2,0,0,1);
    o.Normal=normalize(mul(float4(UnpackGrassNormal(v.PackedNormal),0),WorldInvTranspose).xyz);
    float3 L=normalize(PointLightDir.xyz-o.WorldPos*PointLightDir.w);
    float3 V=normalize(CameraPosition.xyz-o.WorldPos);
    o.Ndl=dot(o.Normal,L);
    o.Backlight=(1.0-unitCorner.y)*saturate(dot(-V,L));
    o.ViewDistance=distance(CameraPosition.xyz,o.WorldPos);
    o.Tint=v.Tint;
    o.DistanceFade=distanceFade;
    return o;
}

float3 DecodeDxt5Normal(float4 s) {
    float2 xy = s.ag * 2.0 - 1.0;
    float z = sqrt(saturate(1.0 - dot(xy,xy)));
    return float3(xy.x, xy.y, z);
}

float3 ToWorldNormal(VSOut i, float3 tangentNormal) {
    float3 N = normalize(i.Normal);
    float3 T = normalize(i.Tangent - N * dot(i.Tangent,N));
    float3 B = normalize(cross(N,T));
    return normalize(tangentNormal.x*T + tangentNormal.y*B + tangentNormal.z*N);
}

float SampleShadowMap(Texture2D map, float4 lightClip) {
    float3 p = lightClip.xyz / max(lightClip.w, 0.00001);
    float2 uv = p.xy * float2(0.5,-0.5) + 0.5;
    float depth = p.z - 0.0008;
    if (any(uv < 0) || any(uv > 1) || depth <= 0 || depth >= 1) return 1;
    float shadow = 0;
    const float texel = 1.0 / 1024.0;
    [unroll] for (int y=-1;y<=1;y++) [unroll] for (int x=-1;x<=1;x++)
        shadow += map.SampleCmpLevelZero(ShadowSampler, uv + float2(x,y)*texel, depth);
    return shadow / 9.0;
}

float CascadeShadow(VSOut i) {
    if (EnableShadows == 0) return 1;
    int cascade = i.ViewDistance < ShadowSplits.x ? 0 : (i.ViewDistance < ShadowSplits.y ? 1 : (i.ViewDistance < ShadowSplits.z ? 2 : 3));
    float4 cp = cascade == 0 ? mul(float4(i.WorldPos,1), ShadowMatrix0) : cascade == 1 ? mul(float4(i.WorldPos,1), ShadowMatrix1) : cascade == 2 ? mul(float4(i.WorldPos,1), ShadowMatrix2) : mul(float4(i.WorldPos,1), ShadowMatrix3);
    float s = cascade == 0 ? SampleShadowMap(ShadowMap0,cp) : cascade == 1 ? SampleShadowMap(ShadowMap1,cp) : cascade == 2 ? SampleShadowMap(ShadowMap2,cp) : SampleShadowMap(ShadowMap3,cp);
    if (cascade < 3) {
        float nearSplit = cascade == 0 ? 0 : (cascade == 1 ? ShadowSplits.x : ShadowSplits.y);
        float split = cascade == 0 ? ShadowSplits.x : (cascade == 1 ? ShadowSplits.y : ShadowSplits.z);
        float band = max(0.1, (split-nearSplit) * 0.18);
        float blend = saturate((i.ViewDistance - (split-band)) / band);
        if (blend > 0) {
            int n = cascade + 1;
            float4 np = n == 1 ? mul(float4(i.WorldPos,1),ShadowMatrix1) : n == 2 ? mul(float4(i.WorldPos,1),ShadowMatrix2) : mul(float4(i.WorldPos,1),ShadowMatrix3);
            float ns = n == 1 ? SampleShadowMap(ShadowMap1,np) : n == 2 ? SampleShadowMap(ShadowMap2,np) : SampleShadowMap(ShadowMap3,np);
            s = lerp(s,ns,blend);
        }
    }
    return s;
}

float2 WrapScrollingUv(float2 uv) { return frac(uv * 0.0625) * 16.0; }
float ScrollingCloudShade(float3 worldPos) {
    if (ScrollingEnabled == 0) return 1;
    float2 texUv = (ScrollingOffsetParams.xy + worldPos.xz) / max(ScrollingVisualParams.x,0.0001);
    float2 maskUv = (ScrollingOffsetParams.zw + worldPos.xz) / max(ScrollingVisualParams.y,0.0001);
    float4 shades = ScrollingTextureMap.Sample(LinearWrap, WrapScrollingUv(texUv));
    float4 weights = ScrollingMaskMap.Sample(LinearWrap, WrapScrollingUv(maskUv)) + 0.0039216;
    weights *= weights;
    weights /= max(dot(weights,float4(1,1,1,1)),0.0001);
    return saturate(ScrollingVisualParams.z * dot(shades,weights) + ScrollingVisualParams.w);
}

float3 RampLight(float ndotl, float shadow) {
    if (HasIllumination != 0) {
        float u = saturate(ndotl * 0.484375 + 0.5);
        float v = shadow * 0.5 + 0.25;
        float3 ramp = IlluminationMap.Sample(LinearClamp,float2(u,v)).rgb;
        return FrontLightColor.rgb * ramp * 1.05 * (shadow * 0.15 * saturate(ndotl) + 0.925);
    }
    return AmbientLightColor.rgb + FrontLightColor.rgb * saturate(ndotl) * shadow;
}

float FresnelRange(float nDotV, float zeroFresnel, float fullFresnel) {
    return saturate((saturate(1.0 - nDotV) - zeroFresnel) / max(fullFresnel-zeroFresnel,0.0001));
}

float3 CalculateRim(float3 color, float3 lightColor, float normalY, float nDotV, float4 glossSample) {
    float specFactor = saturate(dot(glossSample,float4(.5,.5,.5,0)));
    float rim = lerp(.15,.75,specFactor);
    rim *= saturate(1.1-normalY*normalY);
    rim *= FresnelRange(nDotV,.5,.9);
    return lerp(color,AdvancedLightingParams.y*lightColor,saturate(rim));
}

float3 AddSkySpecular(float3 N, float3 V, float3 lightColor, float4 glossSample) {
    float3 reflectedView=normalize(reflect(-V,N));
    float skyShade=.5*reflectedView.y+.5;
    float skySpecPower=glossSample.a*16.0+3.0;
    float skyEnergy=saturate(dot(glossSample.rgb,float3(.4,.5,.1)))*-.6+1.0;
    return AdvancedLightingParams.z*lightColor*glossSample.rgb*skyEnergy*pow(saturate(skyShade),skySpecPower);
}

float3 SampleLocalIllumination(int n, float2 uv) {
    if(n==0) return LocalLightIlluminationMap0.SampleLevel(LinearClamp,uv,0).rgb;
    if(n==1) return LocalLightIlluminationMap1.SampleLevel(LinearClamp,uv,0).rgb;
    if(n==2) return LocalLightIlluminationMap2.SampleLevel(LinearClamp,uv,0).rgb;
    return LocalLightIlluminationMap3.SampleLevel(LinearClamp,uv,0).rgb;
}

float3 SampleLocalFalloff(int n, float2 uv) {
    if(n==0) return LocalLightFalloffMap0.SampleLevel(LinearClamp,uv,0).rgb;
    if(n==1) return LocalLightFalloffMap1.SampleLevel(LinearClamp,uv,0).rgb;
    if(n==2) return LocalLightFalloffMap2.SampleLevel(LinearClamp,uv,0).rgb;
    return LocalLightFalloffMap3.SampleLevel(LinearClamp,uv,0).rgb;
}

float3 SampleLocalRamp(int n, float2 uv) {
    if(n==0) return LocalLightRampMap0.SampleLevel(LinearClamp,uv,0).rgb;
    if(n==1) return LocalLightRampMap1.SampleLevel(LinearClamp,uv,0).rgb;
    if(n==2) return LocalLightRampMap2.SampleLevel(LinearClamp,uv,0).rgb;
    return LocalLightRampMap3.SampleLevel(LinearClamp,uv,0).rgb;
}

float3 ProjectedLocalLightMask(int n, float3 worldPos) {
    float4 flags=LocalLightProjectorParams[n];

    // Jedipedia always applies the authored projector volume, even when the illumination/falloff
    // textures themselves are missing (its missing textures resolve to white). Previously PugTools
    // skipped these bounds entirely and replaced them with a synthetic spherical fade, which made
    // many indoor .lit volumes either disappear or affect the wrong surfaces.
    float4 local=mul(float4(worldPos,1),LocalLightProjectorInv[n]);
    if(local.w<=0.0) return float3(0,0,0);
    float3 projected=local.xyz/max(local.w,0.0001)*0.5+0.5;
    if(any(projected.xy<float2(0,0)) || any(projected.xy>float2(1,1))) return float3(0,0,0);

    float rearProjectionRange=max(1.0-flags.x,0.0001);
    float compressedRear=(0.5*rearProjectionRange-0.5+projected.z)/rearProjectionRange;
    float offsetZ=projected.z>0.5?projected.z:compressedRear;
    if(offsetZ<0.0 || offsetZ>1.0) return float3(0,0,0);

    float3 mask=float3(1,1,1);
    if(flags.y>=0.5) mask*=SampleLocalIllumination(n,projected.xy);
    if(flags.z>=0.5) {
        // The shipped local Uber/Grass passes pin the second falloff coordinate to the centre row.
        mask*=SampleLocalFalloff(n,float2(offsetZ,0.5));
    }
    return mask;
}

float3 LocalLighting(VSOut i, float3 N, float3 V, float3 baseColor) {
    float3 sum = float3(0,0,0);
    [loop] for (int n=0;n<4;n++) {
        if (n >= LocalLightCount) break;
        float type = LocalLightDirType[n].w;
        float3 L;
        float attenuation = 1;
        if (type > 0.5 && type < 1.5) {
            L = normalize(LocalLightDirType[n].xyz);
        } else {
            float3 delta = LocalLightPosRange[n].xyz - i.WorldPos;
            float dist = length(delta); L = delta / max(dist,0.0001);
            // SWTOR/Jedipedia shapes ordinary OMNI/BOX lights with the projector and authored falloff
            // texture. The old extra quadratic radial fade attenuated the same light a second time and
            // was the main reason interiors remained almost black even when a .lit volume was selected.
            if (type >= 1.5) {
                float range = max(LocalLightPosRange[n].w,0.001);
                attenuation = saturate(1 - dist/range); attenuation *= attenuation;
                float3 spotDir = normalize(LocalLightDirType[n].xyz);
                float cone = saturate((dot(-L,spotDir)-0.55)/0.35);
                attenuation *= cone*cone;
            }
        }
        float ndl = saturate(dot(N,L));
        float3 projectorMask=ProjectedLocalLightMask(n,i.WorldPos);
        if(dot(projectorMask,float3(1,1,1))<=0.0001) continue;

        // Local lights have their own authored ramp in SWTOR.  The game samples it with the same
        // 0.484375 incidence mapping as the environment ramp and the fully-lit V row.
        // Jedipedia always has a ramp here: a missing authored RampMap is a 1x1 white fallback, not
        // Lambert lighting. That distinction is large indoors because the game's local-light pass is nearly
        // full-energy even at grazing incidence; the ramp carries the authored shape.
        float3 ramp=float3(1,1,1);
        if(LocalLightProjectorParams[n].w>=0.5)
            ramp=SampleLocalRamp(n,float2(saturate(ndl*0.484375+0.5),0.75));
        float3 authoredLighting=ramp*1.05*(0.15*ndl+0.925);
        float3 H = normalize(L+V);
        float spec = pow(saturate(dot(N,H)),24);
        float3 lightEnergy=LocalLightColorIntensity[n].rgb*LocalLightColorIntensity[n].a*attenuation*projectorMask;
        sum += (baseColor*authoredLighting + float3(spec,spec,spec)*.22) * lightEnergy;
    }
    return sum;
}

float CalcFogBlend(float distanceToCamera) {
    float normalizedDistance=saturate((distanceToCamera-FogParams.z)/max(FogParams.w,0.0001));
    float normalizedEarlyRamp=FogParams.z==0 ? 1 : saturate(distanceToCamera/FogParams.z);
    return normalizedDistance*FogParams.y + FogParams.x*normalizedEarlyRamp;
}

float4 ApplyFog(float4 color, VSOut i) {
    if (EnableFog == 0) return color;
    float skyBlend=FogColorParams.z>0.5 ? saturate(1.75*(i.WorldPos.y-CameraPosition.y)/max(i.ViewDistance,0.0001)) : 0;
    float fogAmount=CalcFogBlend(i.ViewDistance)*lerp(1.0,FogColorSky.a,skyBlend);
    float colorBlend=FogColorParams.z>0.5 ? saturate((i.ViewDistance-FogColorParams.x)/max(FogColorParams.y,0.0001)) : 0;
    float3 fog=FogColorParams.z>0.5 ? lerp(lerp(FogColor0.rgb,FogColor1.rgb,colorBlend),FogColorSky.rgb,skyBlend) : FogColor0.rgb;
    color.rgb=lerp(color.rgb,fog,saturate(fogAmount));
    return color;
}


float4 DynamicDetailPS(DydOut i):SV_Target {
    if(i.DistanceFade<0.001)discard;
    // Match SWTOR/Jedipedia's DynamicDetailGrass cutout path: a billboard without its authored diffuse texture must
    // not fall back to a solid coloured card, and surviving texels are opaque depth-writing coverage after AlphaRef.
    // This is the important distinction between real grass silhouettes and the large green rectangles seen when a
    // streamed/failed material was rendered without its texture.
    if(HasDiffuse==0)discard;
    float4 diffuse=DiffuseMap.Sample(LinearWrap,i.Tex);
    if(diffuse.a<(AlphaTestValue>0?AlphaTestValue:0.5))discard;
    VSOut baseInput;
    baseInput.Pos=float4(0,0,0,1);baseInput.WorldPos=i.WorldPos;baseInput.Normal=i.Normal;baseInput.Tangent=float3(1,0,0);baseInput.Tex=i.Tex;baseInput.ViewDistance=i.ViewDistance;
    float shade=CascadeShadow(baseInput)*ScrollingCloudShade(i.WorldPos);
    float3 ramp=EnableLighting!=0?RampLight(i.Ndl,shade):float3(1,1,1);
    float3 backlight=EnableLighting!=0?i.Tint*i.Backlight*shade*ramp*DydParams.w:float3(0,0,0);
    float3 rgb=diffuse.rgb*i.Tint*(ramp+backlight);
    if(EnableLighting!=0){
        float3 V=normalize(CameraPosition.xyz-i.WorldPos);
        rgb+=LocalLighting(baseInput,normalize(i.Normal),V,diffuse.rgb*i.Tint);
        float toneExp=max(1.08-0.13*(shade*i.Ndl+dot(backlight,float3(.299,.587,.114))),0.0001);rgb=pow(max(rgb,float3(0,0,0)),float3(toneExp,toneExp,toneExp));
    }
    return ApplyFog(float4(rgb,1.0),baseInput);
}
float4 DynamicDetailShadowPS(DydOut i):SV_Target {
    if(HasDiffuse==0)discard;
    float4 diffuse=DiffuseMap.Sample(LinearWrap,i.Tex);
    if(i.DistanceFade<0.001||diffuse.a<(AlphaTestValue>0?AlphaTestValue:0.5))discard;
    return 0;
}

void EvaluateMaterial(VSOut i, out float4 c, out float3 N, out float4 glossSample) {
    float2 uv=i.Tex*MaterialUvScale;
    float4 d=(HasDiffuse!=0 ? DiffuseMap.Sample(LinearWrap,uv) : float4(.65,.67,.68,1))*MaterialFlatColor;
    float4 rot=HasRotation!=0 ? RotationMap.Sample(LinearWrap,uv) : float4(0.5,0.5,1,0);
    float alphaValue=HasRotation!=0 ? 1.0-rot.r : d.a;
    N=HasRotation!=0 ? ToWorldNormal(i,DecodeDxt5Normal(rot)) : normalize(i.Normal);
    glossSample=HasGloss!=0 ? GlossMap.Sample(LinearWrap,uv) : float4(.2,.2,.2,.25);

    if(HasEnvBlend!=0){
        float2 envUv=uv*(EnvBlendParams2.y*EnvBlendMaterialTileMult);
        float4 envDiffuse=EnvBlendDiffuseMap.Sample(LinearWrap,envUv);
        float4 envNormal=EnvBlendNormalMap.Sample(LinearWrap,envUv);
        float3 envN=ToWorldNormal(i,DecodeDxt5Normal(envNormal));
        float3 geomN=normalize(i.Normal);
        float heightTerm=N.y-geomN.y;
        heightTerm=EnvBlendParams1.z*heightTerm+geomN.y;
        heightTerm=saturate(heightTerm+EnvBlendParams1.w)-.5;
        float blend=saturate(heightTerm*EnvBlendParams2.x+envDiffuse.a);
        if(EnvBlendEnableCreviceMap!=0) blend=saturate(blend+saturate((.5-alphaValue)*EnvBlendParams2.w+.5));
        float normalBlend=saturate(blend-EnvBlendParams2.z)*saturate(EnvBlendParams1.y);
        N=normalize(lerp(N,envN,normalBlend));
        d.rgb=lerp(d.rgb,envDiffuse.rgb,saturate(blend*EnvBlendParams1.y));
        glossSample.rgb=lerp(glossSample.rgb,float3(envNormal.r,envNormal.r,envNormal.r),blend);
        glossSample.a=lerp(glossSample.a,EnvBlendParams1.x,blend);
    }
    c=float4(d.rgb,min(DiffuseTextureAlphaMax,alphaValue));
    if (MaterialIsOpacityFade != 0) {
        // SWTOR OpacityFade ignores texture alpha and replaces it with the authored distance ramp.
        float fadeRange=max(OpacityFadeParams.z-OpacityFadeParams.x,0.0001);
        float normalizedDistance=saturate((i.ViewDistance-OpacityFadeParams.x)/fadeRange);
        float fadeAlpha=normalizedDistance*saturate(OpacityFadeParams.w-OpacityFadeParams.y)+OpacityFadeParams.y;
        c.a=saturate(fadeAlpha);
        // None/Test + cutoff is the hard-cutout style. Full is blended and keeps the ramp as real alpha.
        if (AlphaMode < 2 && AlphaTestValue > 0 && c.a < AlphaTestValue) discard;
    } else if (AlphaMode == 1 && c.a < AlphaTestValue) discard;
}

float3 ApplyViewerBlueGlow(float3 rgb, float alphaValue) {
    if(ViewerBlueGlow<=0.5) return rgb;
    // Match Jedipedia's injected interaction tint for the corresponding SWTOR blend families.
    if(AlphaMode<=1) return float3(0.0,0.28,0.9)+rgb*0.55;
    if(AlphaMode==2) return rgb+float3(0.0,0.28,0.9);
    if(AlphaMode==3) return lerp(rgb,float3(0.18,0.45,1.0),0.55);
    return float3(0.0,0.28,0.9)*alphaValue+rgb*0.55;
}

float4 LitPS(VSOut i):SV_Target {
    float4 c; float3 N; float4 glossSample; EvaluateMaterial(i,c,N,glossSample);
    float3 V=normalize(CameraPosition.xyz-i.WorldPos);
    float3 lightDirection=normalize(PointLightDir.xyz);
    float ndl=dot(N,lightDirection);
    float shade=CascadeShadow(i)*ScrollingCloudShade(i.WorldPos);
    float3 lightColor=EnableLighting!=0 ? RampLight(ndl,shade) : float3(1,1,1);
    float3 reflectedLight=reflect(-lightDirection,N);
    float specDot=max(dot(reflectedLight,V),0);
    float3 specColor=glossSample.rgb*lightColor*saturate(2.0*shade+0.3333)*pow(specDot,glossSample.a*63.0+1.0);
    float3 outColor=c.rgb*lightColor+specColor;
    outColor=CalculateRim(outColor,lightColor,N.y,abs(dot(N,V)),glossSample);
    outColor+=AddSkySpecular(N,V,lightColor,glossSample);
    outColor+=LocalLighting(i,N,V,c.rgb);
    // SWTOR's Uber-family #emissive variant takes its mask from RotationMap.b. Stronghold hook fields
    // rely heavily on this: without it their holographic colors collapse to dull grey/black.
    if(MaterialUsesEmissive!=0){
        float emissiveLum=HasRotation!=0 ? RotationMap.Sample(LinearWrap,i.Tex*MaterialUvScale).b : 1.0;
        emissiveLum=saturate(emissiveLum);
        outColor*=1.0-emissiveLum;
        outColor+=c.rgb*emissiveLum;
    }
    outColor=ApplyViewerBlueGlow(outColor,c.a);
    return ApplyFog(float4(outColor,c.a),i);
}

// Jedipedia/SWTOR local lights are additive material re-draws after the ordinary environment pass. The
// first PugTools pass still evaluates four lights in-place for efficiency; this shader is used only for receiver
// lights beyond those four, so it must output local-light energy alone (no fog/environment/emissive duplication).
float4 LocalLightAddPS(VSOut i):SV_Target {
    float4 c; float3 N; float4 glossSample; EvaluateMaterial(i,c,N,glossSample);
    float3 V=normalize(CameraPosition.xyz-i.WorldPos);
    return float4(LocalLighting(i,N,V,c.rgb),0);
}

float4 HookOverlayPS(VSOut i):SV_Target {
    float4 c; float3 n; float4 g; EvaluateMaterial(i,c,n,g);
    // Stronghold hook meshes are editor-style holographic guides, not ordinary scene materials.
    // Keep their authored mask/texture, but make the size-class tint readable and translucent like Jedipedia.
    float alpha=saturate(max(c.a,0.12)*HookOpacity);
    float3 rgb=c.rgb;
    if(dot(rgb,rgb)<0.001) rgb=MaterialFlatColor.rgb;
    rgb=saturate(max(rgb,MaterialFlatColor.rgb*0.35)*1.22);
    return float4(rgb,alpha);
}

float4 HookAdditivePS(VSOut i):SV_Target {
    float4 c; float3 n; float4 g; EvaluateMaterial(i,c,n,g);
    // A number of hook marker MATs are AnimatedVFX/Add. Their diffuse RGB is intentionally black and
    // the alpha/mask carries the beam. Reconstruct that bright core with the hook tint instead of white;
    // this preserves Small/Medium/Large green/blue/purple while still looking emissive.
    float intensity=saturate(max(c.a,max(c.r,max(c.g,c.b))));
    float3 core=MaterialFlatColor.rgb*intensity;
    float3 rgb=max(c.rgb,core*0.70);
    return float4(rgb*HookOpacity,intensity);
}

float4 UnlitPS(VSOut i):SV_Target { float4 c; float3 n; float4 g; EvaluateMaterial(i,c,n,g); c.rgb=ApplyViewerBlueGlow(c.rgb,c.a); return ApplyFog(c,i); }
float4 SkyPS(VSOut i):SV_Target {
    // Jedipedia/SWTOR Skydome.fx is intentionally much simpler than the ordinary material path:
    // sample DiffuseMap with the authored primary UVs, output RGB, force opaque alpha. Applying tint/alpha/fog
    // material logic here is what turned several real skydomes (notably Dantooine) into flat orange/black.
    float4 c = HasDiffuse != 0 ? DiffuseMap.Sample(LinearWrap, i.Tex) : float4(0,0,0,1);
    return float4(c.rgb, 1.0);
}

void EvaluateTerrain(VSOut i, out float3 diffuseColor, out float3 N, out float4 glossSample, out float layerWeight) {
    // Jedipedia/TerrainAntiTile uses the heightmap node's generated tiled UV for material maps.
    // The vertex data already spans width/2 by depth/2 tiles; using absolute world XZ here changed
    // both scale and phase and made neighbouring nodes/materials look like fallback/checker terrain.
    float2 uv=i.Tex*MaterialUvScale*TerrainUvScale;
    float2 primary=uv;
    float2 secondary=uv.yx*0.67;
    float2 selectorUv=uv*0.075358;
    float2 chunkUv=saturate(i.Tex*TerrainChunkScale);
    layerWeight=HasTerrainMask!=0 ? TerrainMask.Sample(LinearClamp,chunkUv).r : 1.0;

    float2 packedNormal=float2(0.5,0.5);
    glossSample=float4(.2,.2,.2,.25);
    if(HasDiffuse2!=0) {
        float4 anti=DiffuseMap2.Sample(LinearWrap,selectorUv);
        // SWTOR's distant anti-tile path chooses the alpha channel and hard-selects one of two
        // differently oriented tile samples. A real DiffuseMap2 is required; otherwise this
        // produces visible seams between heightmap nodes.
        float selector=floor(anti.a+0.5);
        float3 d0=HasDiffuse!=0 ? DiffuseMap.Sample(LinearWrap,primary).rgb : float3(.45,.5,.38);
        float3 d1=HasDiffuse!=0 ? DiffuseMap.Sample(LinearWrap,secondary).rgb : d0;
        diffuseColor=lerp(d0,d1,selector);
        if(HasRotation!=0) {
            float2 n0=RotationMap.Sample(LinearWrap,primary).ag;
            float2 n1=1.0-RotationMap.Sample(LinearWrap,secondary).ga;
            packedNormal=lerp(n0,n1,selector);
        }
        if(HasGloss!=0) glossSample=lerp(GlossMap.Sample(LinearWrap,primary),GlossMap.Sample(LinearWrap,secondary),selector);
        float distantBlend=saturate((i.ViewDistance-1.5)*0.25);
        diffuseColor=lerp(diffuseColor,anti.rgb,distantBlend);
    } else {
        diffuseColor=HasDiffuse!=0 ? DiffuseMap.Sample(LinearWrap,primary).rgb : float3(.45,.5,.38);
        if(HasRotation!=0) packedNormal=RotationMap.Sample(LinearWrap,primary).ag;
        if(HasGloss!=0) glossSample=GlossMap.Sample(LinearWrap,primary);
    }

    float2 bump=packedNormal*2.0-1.0;
    float3 tangentNormal=float3(bump,sqrt(saturate(1.0-dot(bump,bump))));
    N=HasRotation!=0 ? ToWorldNormal(i,tangentNormal) : normalize(i.Normal);
    if(HasTerrainColor!=0) diffuseColor*=TerrainColorMap.Sample(LinearClamp,chunkUv).rgb;
    layerWeight=saturate(layerWeight)*min(1.0,DiffuseTextureAlphaMax);
}

float4 TerrainCoveragePS(VSOut i):SV_Target { return float4(0,0,0,1); }

float4 TerrainLitPS(VSOut i):SV_Target {
    float3 diffuseColor,N;float4 glossSample;float layerWeight;
    EvaluateTerrain(i,diffuseColor,N,glossSample,layerWeight);
    float3 toCamera=CameraPosition.xyz-i.WorldPos;float distanceToCamera=length(toCamera);float3 V=normalize(toCamera);
    float3 lightDirection=normalize(PointLightDir.xyz-i.WorldPos*PointLightDir.w);
    float ndl=dot(N,lightDirection);
    float shade=CascadeShadow(i)*ScrollingCloudShade(i.WorldPos);
    float3 lightColor=EnableLighting!=0 ? RampLight(ndl,shade) : float3(1,1,1);
    float3 reflectedLight=reflect(-lightDirection,N);
    float specDot=max(dot(reflectedLight,V),0.0);
    float3 specColor=EnableLighting!=0 ? glossSample.rgb*lightColor*pow(specDot,glossSample.a*63.0+1.0) : float3(0,0,0);
    float3 outColor=diffuseColor*lightColor+specColor;
    if(EnableLighting!=0) {
        outColor=CalculateRim(outColor,lightColor,N.y,abs(dot(N,V)),glossSample);
        outColor+=AddSkySpecular(N,V,lightColor,glossSample);
        // Ground-truth TerrainAntiTile first-pass tone/exposure curve.
        float toneExp=ndl*shade*-0.13+1.08;
        outColor=pow(max(outColor,float3(0,0,0)),float3(toneExp,toneExp,toneExp));
        // PugTools renders the game's extra light passes approximately in one pass.
        outColor+=LocalLighting(i,N,V,diffuseColor);
    }
    float4 fogged=ApplyFog(float4(outColor,1),i);
    fogged.rgb*=layerWeight;
    return float4(fogged.rgb,1);
}
float4 TerrainLocalLightAddPS(VSOut i):SV_Target {
    float3 diffuseColor,N;float4 glossSample;float layerWeight;EvaluateTerrain(i,diffuseColor,N,glossSample,layerWeight);
    float3 V=normalize(CameraPosition.xyz-i.WorldPos);
    return float4(LocalLighting(i,N,V,diffuseColor)*layerWeight,0);
}

float4 TerrainUnlitPS(VSOut i):SV_Target {
    float3 diffuseColor,N;float4 glossSample;float layerWeight;EvaluateTerrain(i,diffuseColor,N,glossSample,layerWeight);
    float4 fogged=ApplyFog(float4(diffuseColor,1),i);fogged.rgb*=layerWeight;return float4(fogged.rgb,1);
}

float4 HeightPS(VSOut i):SV_Target {
    float t=saturate((i.WorldPos.y-HeightRange.x)/max(HeightRange.y-HeightRange.x,.001));
    float3 c=lerp(float3(.05,.15,.35),float3(.15,.7,.25),saturate(t*1.7));
    c=lerp(c,float3(.75,.65,.25),saturate((t-.45)*2)); c=lerp(c,float3(1,1,1),saturate((t-.75)*4));
    return float4(c,1);
}

float2 ModifiedWaterCoords(float2 origCoords, float angle, float2 offsetCoords) {
    float sn=sin(angle), cs=cos(angle);
    return float2(cs*origCoords.x-sn*origCoords.y, sn*origCoords.x+cs*origCoords.y)+offsetCoords;
}

float WaterRawDepth(float2 uv) {
    return HasWaterDepth != 0 ? WaterDepthMap.Sample(LinearClamp,uv).a : 0.627451;
}

float2 WaterDepthValue(float2 uv) {
    float depthValue=WaterRawDepth(uv);
    // SWTOR computes this specular knee ramp before the piecewise alpha remap.
    float kneeRamp=(depthValue-WaterAlphaParams1.x)/max(WaterAlphaParams1.y-WaterAlphaParams1.x,0.0001);
    if(depthValue<=WaterAlphaParams1.x)
        depthValue=WaterAlphaParams2.x*depthValue/max(WaterAlphaParams1.x,0.0001);
    else if(depthValue<=WaterAlphaParams1.y)
        depthValue=WaterAlphaParams2.x+(WaterAlphaParams2.y-WaterAlphaParams2.x)*(depthValue-WaterAlphaParams1.x)/max(WaterAlphaParams1.y-WaterAlphaParams1.x,0.0001);
    else if(depthValue<=WaterAlphaParams1.z)
        depthValue=WaterAlphaParams2.y+(WaterAlphaParams2.z-WaterAlphaParams2.y)*(depthValue-WaterAlphaParams1.y)/max(WaterAlphaParams1.z-WaterAlphaParams1.y,0.0001);
    else
        depthValue=WaterAlphaParams2.z+(WaterAlphaParams2.w-WaterAlphaParams2.z)*(depthValue-WaterAlphaParams1.z)/max(1.0-WaterAlphaParams1.z,0.0001);
    return float2(saturate(depthValue),kneeRamp);
}

float WaterSurfaceDepthValue(float2 uv) {
    float depthValue=WaterRawDepth(uv);
    if(depthValue<=WaterAlphaParams3.x)
        depthValue=WaterAlphaParams3.w+(WaterAlphaParams4.x-WaterAlphaParams3.w)*depthValue/max(WaterAlphaParams3.x,0.0001);
    else if(depthValue<=WaterAlphaParams3.y)
        depthValue=WaterAlphaParams4.x+(WaterAlphaParams4.y-WaterAlphaParams4.x)*(depthValue-WaterAlphaParams3.x)/max(WaterAlphaParams3.y-WaterAlphaParams3.x,0.0001);
    else if(depthValue<=WaterAlphaParams3.z)
        depthValue=WaterAlphaParams4.y+(WaterAlphaParams4.z-WaterAlphaParams4.y)*(depthValue-WaterAlphaParams3.y)/max(WaterAlphaParams3.z-WaterAlphaParams3.y,0.0001);
    else
        depthValue=WaterAlphaParams4.z+(WaterAlphaParams4.w-WaterAlphaParams4.z)*(depthValue-WaterAlphaParams3.z)/max(1.0-WaterAlphaParams3.z,0.0001);
    return saturate(depthValue);
}

float4 WaterPS(VSOut i):SV_Target {
    const float MAX_POWER=64.0;
    const float MAX_DIST_ALPHA=0.75;
    const float SURFACE_MAP_NORMALDISPLACEMENT=0.01;

    float2 texCoord=i.Tex;
    float2 norm1TexCoords=WaterParams3.x*i.WorldPos.xz;
    float2 norm2TexCoords=WaterParams3.y*i.WorldPos.xz;
    float2 surfaceTexCoords=WaterParams3.z*i.WorldPos.xz;
    float surfaceDepthMapValue=WaterSurfaceDepthValue(texCoord);
    float2 water1=ModifiedWaterCoords(norm1TexCoords,WaterParams.x,WaterNmOffsets.xy);
    float2 water2=ModifiedWaterCoords(norm2TexCoords,WaterParams.y,WaterNmOffsets.zw);

    float2 distortionVector=HasWaterNormal1!=0 ? WaterNormalMap1.Sample(LinearWrap,surfaceTexCoords).ag*2.0-1.0 : float2(0,0);
    float2 uvDistortion=distortionVector*0.2;
    float2 bump1=HasWaterNormal1!=0 ? WaterNormalMap1.Sample(LinearWrap,water1+uvDistortion).ag*2.0-1.0 : float2(0,0);
    float2 bump2=HasWaterNormal2!=0 ? WaterNormalMap2.Sample(LinearWrap,water2+uvDistortion).ag*2.0-1.0 : bump1;
    float3 normalValue=normalize(float3((bump1+bump2)*WaterParams2.y,1.0));
    surfaceTexCoords+=normalValue.xy*WaterParams3.z*SURFACE_MAP_NORMALDISPLACEMENT;
    float4 surfaceValue=HasWaterSurface!=0 ? WaterSurfaceMap.Sample(LinearWrap,float3(surfaceTexCoords,surfaceDepthMapValue)) : float4(0,0,0,0);

    // Jedipedia follows SWTOR's unusual xzy water-light space here.
    float3 illum=normalize(PointLightDir.xyz).xzy;
    float cloudShade=ScrollingCloudShade(i.WorldPos);
    float shade=min(illum.z,cloudShade*0.4+0.6);
    float3 ramp=HasIllumination!=0 ? IlluminationMap.Sample(LinearClamp,float2(shade*0.5+0.5,1.0)).rgb : float3(max(shade,0.15),max(shade,0.15),max(shade,0.15));
    float3 lightColor=EnableLighting!=0 ? FrontLightColor.rgb*ramp : float3(1,1,1);

    float3 toCamera=CameraPosition.xyz-i.WorldPos;
    float distanceToCamera=length(toCamera);
    toCamera=normalize(toCamera);
    float3 camAngle=toCamera.xzy;

    float3 reflectedIllum=reflect(float3(0.0,1.0,0.0),normalValue);
    float specDot=saturate(dot(reflectedIllum,camAngle));
    float specPower=WaterAlphaParams1.w*(MAX_POWER-1.0)+1.0;
    float3 specCoeff=WaterGlossColor.rgb*2.0*pow(specDot,specPower);
    float cosTheta=camAngle.z;
    float angleModulator=1.0-WaterParams2.x*cosTheta;
    float2 depthRet=WaterDepthValue(texCoord);
    float depthMapValue=depthRet.x;
    float distanceAlpha=angleModulator*min(1.0,3.0*depthMapValue)*min(MAX_DIST_ALPHA,max(0.0,(WaterParams.w*distanceToCamera-5.0)/20.0));

    float3 color=lerp(WaterShallowColor.rgb,WaterDeepColor.rgb,depthMapValue);
    color=surfaceValue.a*surfaceValue.rgb+(1.0-surfaceValue.a)*color;
    depthMapValue=min(depthMapValue,angleModulator);
    color+=specCoeff.r*(1.0-(1.0-WaterParams2.w)*surfaceValue.a)*WaterParams3.w;
    color*=lightColor;
    if(EnableLighting!=0 && LocalLightCount>0) {
        // Water's authored normal is evaluated in SWTOR's xzy light space; swizzle it back before
        // feeding the shared projected-local-light path. DoWater is enforced by the receiver cull.
        float3 waterWorldNormal=normalize(float3(normalValue.x,normalValue.z,normalValue.y));
        color+=LocalLighting(i,waterWorldNormal,normalize(CameraPosition.xyz-i.WorldPos),color);
    }

    toCamera=toCamera.xzy;
    float normalHeightMask=0.1+toCamera.z*toCamera.z;
    normalValue.xy*=normalHeightMask;
    normalValue=normalize(normalValue);
    float reflectionCos=saturate(min(0.4,dot(normalValue,toCamera)));
    float reflectionLookDownMask=(1.0-normalValue.z)*10.0;
    float reflectionAlpha=(pow(1.0-reflectionCos,4.0)+reflectionLookDownMask)*min(1.0,depthMapValue*1.75)*WaterParams3.w;
    if(HasWaterEnvironment!=0 && reflectionAlpha>0.0001) {
        float3 reflectionVector=normalize(reflect(-toCamera,normalValue)).xzy;
        float bias=min(2.7,distanceToCamera*0.75);
        float3 envColor=WaterEnvironmentMap.SampleLevel(LinearClamp,reflectionVector,bias).rgb;
        color=lerp(color,envColor,saturate(reflectionAlpha));
    }
    float alpha=max(reflectionAlpha,max(distanceAlpha,depthMapValue));
    alpha+=specCoeff.r*depthRet.y;
    return ApplyFog(float4(color,saturate(alpha)),i);
}

float4 MapArtPS(VSOut i):SV_Target { float4 c=MapArtMap.Sample(LinearClamp,i.Tex); c*=MapArtTint; c.a*=MapArtOpacity; return c; }
float4 OverlayPS(VSOut i):SV_Target { return OverlayColor; }
float4 ShadowVS(VSIn v):SV_POSITION { return mul(mul(float4(v.Pos,1),World),ViewProj); }
float4 InstancedAlphaShadowPS(VSOut i):SV_Target {
    // The same instanced shadow technique serves opaque and cutout world batches. Only AlphaMode=Test should punch
    // holes in the shadow; an opaque texture may still carry unrelated alpha data.
    if(AlphaMode==1 && HasDiffuse!=0) { float alpha=DiffuseMap.Sample(LinearWrap,i.Tex*MaterialUvScale).a; if(alpha<(AlphaTestValue>0?AlphaTestValue:0.5)) discard; }
    return 0;
}

struct PostOut { float4 Pos:SV_POSITION; float2 Tex:TEXCOORD0; };
PostOut PostVS(uint id:SV_VertexID){
    PostOut o;
    float2 p=id==0?float2(-1,-1):(id==1?float2(-1,3):float2(3,-1));
    o.Pos=float4(p,0,1); o.Tex=float2(p.x*.5+.5, .5-p.y*.5); return o;
}
struct NameplateVisibilityOut {
    float4 Pos : SV_POSITION;
    float3 Sample : TEXCOORD0;
};

NameplateVisibilityOut NameplateVisibilityVS(uint vertexId : SV_VertexID) {
    NameplateVisibilityOut o;
    float2 viewport=max(NameplateVisibilityParams.xy,float2(1,1));
    // The visibility render target is only 192x1, but the normal scene viewport is deliberately
    // retained. Place vertex N at main-viewport pixel (N,0), which maps directly to target texel N
    // without needing a second SlimDX Viewport object.
    float2 pixel=float2((float)vertexId+0.5,0.5);
    float2 ndc=float2(pixel.x/viewport.x*2.0-1.0,1.0-pixel.y/viewport.y*2.0);
    o.Pos=float4(ndc,0,1);
    o.Sample=NameplateSamples[vertexId].xyz;
    return o;
}

float4 NameplateVisibilityPS(NameplateVisibilityOut i) : SV_Target {
    // Same five-tap cross as Jedipedia: a one-pixel cable/railing crossing the anchor should not
    // make the complete label flicker. Any unobstructed sample counts as visible.
    const float2 taps[5]={
        float2(0,0),float2(3,0),float2(-3,0),float2(0,3),float2(0,-3)
    };
    float visible=0;
    [unroll] for(int n=0;n<5;n++) {
        float2 uv=saturate(i.Sample.xy+taps[n]*NameplateVisibilityParams.zw);
        float sceneZ=SceneDepthMap.SampleLevel(PointClamp,uv,0).r;
        if(i.Sample.z<=sceneZ+NameplateDepthBias) visible=1;
    }
    return float4(visible,0,0,1);
}

struct ObjectVisibilityOut {
    float4 Pos : SV_POSITION;
    float4 Sample : TEXCOORD0;
};

ObjectVisibilityOut ObjectVisibilityVS(uint vertexId : SV_VertexID) {
    ObjectVisibilityOut o;
    float outputWidth=max(ObjectVisibilityParams.x,1.0);
    float x=((float(vertexId)+0.5)/outputWidth)*2.0-1.0;
    o.Pos=float4(x,0,0,1);
    o.Sample=ObjectVisibilitySamples[vertexId];
    return o;
}

float4 ObjectVisibilityPS(ObjectVisibilityOut i) : SV_Target {
    // This is deliberately more conservative than a screen-space bounding-box query. A receiver is considered
    // hidden only when the nearest point of its bounding sphere is behind scene depth at thirteen samples spread
    // across the projected disk. Samples outside the real mesh see background and therefore fail open.
    const float2 taps[13]={
        float2(0,0),
        float2(.78,0),float2(-.78,0),float2(0,.78),float2(0,-.78),
        float2(.55,.55),float2(-.55,.55),float2(.55,-.55),float2(-.55,-.55),
        float2(.32,0),float2(-.32,0),float2(0,.32),float2(0,-.32)
    };
    float2 invViewport=1.0/max(ObjectVisibilityParams.yz,float2(1,1));
    float2 radiusUv=i.Sample.w*invViewport;
    [unroll] for(int n=0;n<13;n++) {
        float2 uv=i.Sample.xy+taps[n]*radiusUv;
        if(any(uv<0.0)||any(uv>1.0)) return float4(1,0,0,1);
        float sceneZ=SceneDepthMap.SampleLevel(PointClamp,uv,0).r;
        if(i.Sample.z<=sceneZ+ObjectVisibilityParams.w) return float4(1,0,0,1);
    }
    return float4(0,0,0,1);
}

float3 PostRgbToYCoCg(float3 c) {
    return float3(0.25*c.r + 0.5*c.g + 0.25*c.b, 0.5*c.r - 0.5*c.b, -0.25*c.r + 0.5*c.g - 0.25*c.b);
}
float3 PostYCoCgToRgb(float3 c) {
    float t=c.x-c.z;
    return float3(t+c.y,c.x+c.z,t-c.y);
}

// Camera-only temporal resolve based on Jedipedia's viewer. The opaque depth reconstructs a current world
// position and TaaReprojection moves it into the previous unjittered frame. Moving objects have no motion vectors,
// so a 3x3 YCoCg variance clamp rejects history that no longer matches the current neighbourhood.
float4 TemporalAAPS(PostOut i):SV_Target {
    const float FILTER_SHARPNESS=3.5;
    const float VARIANCE_GAMMA=1.25;
    float2 uv=i.Tex;
    float2 texel=TaaParams0.xy;
    float2 jitter=TaaParams0.zw;
    float4 centre=SceneColorMap.Sample(LinearClamp,uv);
    float depth=SceneDepthMap.SampleLevel(PointClamp,uv,0).r;

    float2 ndc=float2(uv.x*2.0-1.0,1.0-uv.y*2.0);
    float4 previousClip=mul(float4(ndc,depth,1.0),TaaReprojection);

    float3 sum=0;
    float3 sumSquares=0;
    float3 filtered=0;
    float filterWeight=0;
    [unroll] for(int y=-1;y<=1;y++) {
        [unroll] for(int x=-1;x<=1;x++) {
            float2 offset=float2((float)x,(float)y);
            float3 tap=SceneColorMap.SampleLevel(LinearClamp,uv+offset*texel,0).rgb;
            float2 delta=offset-jitter;
            float weight=exp(-FILTER_SHARPNESS*dot(delta,delta));
            filtered+=tap*weight; filterWeight+=weight;
            float3 yc=PostRgbToYCoCg(tap); sum+=yc; sumSquares+=yc*yc;
        }
    }
    float4 current=float4(filtered/max(filterWeight,0.00001),centre.a);
    if(TaaParams1.y<0.5 || previousClip.w<=0.00001) return current;

    float2 previousNdc=previousClip.xy/previousClip.w;
    float2 previousUv=float2(previousNdc.x*0.5+0.5,0.5-previousNdc.y*0.5);
    if(any(previousUv<0.0) || any(previousUv>1.0)) return current;

    float3 mean=sum/9.0;
    float3 deviation=sqrt(max(sumSquares/9.0-mean*mean,0.0));
    float3 minimum=mean-VARIANCE_GAMMA*deviation;
    float3 maximum=mean+VARIANCE_GAMMA*deviation;
    float3 history=clamp(PostRgbToYCoCg(HistoryColorMap.SampleLevel(LinearClamp,previousUv,0).rgb),minimum,maximum);
    float3 resolved=lerp(PostRgbToYCoCg(current.rgb),history,saturate(TaaParams1.x));
    return float4(PostYCoCgToRgb(resolved),current.a);
}

float PostLuma(float3 c) { return dot(c,float3(0.299,0.587,0.114)); }
float4 FxaaPS(PostOut i):SV_Target {
    const float SPAN_MAX=8.0;
    const float REDUCE_MUL=1.0/8.0;
    const float REDUCE_MIN=1.0/128.0;
    const float CONTRAST_THRESHOLD=1.0/16.0;
    const float CONTRAST_MINIMUM=1.0/32.0;
    float2 uv=i.Tex, texel=TaaParams0.xy;
    float4 centre=SceneColorMap.SampleLevel(LinearClamp,uv,0);
    float lumaM=PostLuma(centre.rgb);
    float lumaNW=PostLuma(SceneColorMap.SampleLevel(LinearClamp,uv+float2(-1,-1)*texel,0).rgb);
    float lumaNE=PostLuma(SceneColorMap.SampleLevel(LinearClamp,uv+float2( 1,-1)*texel,0).rgb);
    float lumaSW=PostLuma(SceneColorMap.SampleLevel(LinearClamp,uv+float2(-1, 1)*texel,0).rgb);
    float lumaSE=PostLuma(SceneColorMap.SampleLevel(LinearClamp,uv+float2( 1, 1)*texel,0).rgb);
    float lumaMin=min(lumaM,min(min(lumaNW,lumaNE),min(lumaSW,lumaSE)));
    float lumaMax=max(lumaM,max(max(lumaNW,lumaNE),max(lumaSW,lumaSE)));
    if(lumaMax-lumaMin<max(CONTRAST_MINIMUM,lumaMax*CONTRAST_THRESHOLD)) return centre;
    float2 direction=float2(-((lumaNW+lumaNE)-(lumaSW+lumaSE)),((lumaNW+lumaSW)-(lumaNE+lumaSE)));
    float reduction=max((lumaNW+lumaNE+lumaSW+lumaSE)*0.25*REDUCE_MUL,REDUCE_MIN);
    float scale=1.0/(min(abs(direction.x),abs(direction.y))+reduction);
    direction=clamp(direction*scale,-SPAN_MAX,SPAN_MAX)*texel;
    float3 inner=0.5*(SceneColorMap.SampleLevel(LinearClamp,uv+direction*(1.0/3.0-0.5),0).rgb+SceneColorMap.SampleLevel(LinearClamp,uv+direction*(2.0/3.0-0.5),0).rgb);
    float3 outer=inner*0.5+0.25*(SceneColorMap.SampleLevel(LinearClamp,uv-direction*0.5,0).rgb+SceneColorMap.SampleLevel(LinearClamp,uv+direction*0.5,0).rgb);
    float lumaOuter=PostLuma(outer);
    return float4((lumaOuter<lumaMin||lumaOuter>lumaMax)?inner:outer,centre.a);
}

float4 PostPS(PostOut i):SV_Target{
    float4 c=SceneColorMap.Sample(LinearClamp,i.Tex);
    if(HasColorLut!=0){
        uint w,h,d; ColorLut.GetDimensions(w,h,d);
        float3 dim=max(float3((float)w,(float)h,(float)d),float3(1,1,1));
        float3 uvw=(saturate(c.rgb)*(dim-1.0)+0.5)/dim;
        c.rgb=ColorLut.Sample(LinearClamp,uvw).rgb;
    }
    return c;
}

technique11 Lit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 SkinnedLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 SkinnedLocalLightAdd { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LocalLightAddPS())); } }
technique11 SkinnedAlphaTestLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 SkinnedAlphaLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 SkinnedAddLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 SkinnedMultiplyLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(MultiplyBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 SkinnedUnlit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,UnlitPS())); } }
technique11 SkinnedWire { pass P0 { SetRasterizerState(WireRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,SkinnedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,UnlitPS())); } }
technique11 LocalLightAdd { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LocalLightAddPS())); } }
technique11 AlphaTestLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 AlphaLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 AddLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 MultiplyLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(MultiplyBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 HookOverlay { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,HookOverlayPS())); } }
technique11 HookAdditive { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,HookAdditivePS())); } }
technique11 Unlit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,UnlitPS())); } }
technique11 Sky { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,SkyPS())); } }
technique11 TerrainCoverage { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,TerrainCoveragePS())); } }
technique11 TerrainLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,TerrainLitPS())); } }
technique11 TerrainAddLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,TerrainLitPS())); } }
technique11 TerrainLocalLightAdd { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,TerrainLocalLightAddPS())); } }
technique11 TerrainUnlit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,TerrainUnlitPS())); } }
technique11 TerrainAddUnlit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,TerrainUnlitPS())); } }
technique11 Height { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,HeightPS())); } }
technique11 Wire { pass P0 { SetRasterizerState(WireRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,UnlitPS())); } }
technique11 Water { pass P0 { SetRasterizerState(WaterRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,WaterPS())); } }
technique11 Overlay { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,OverlayPS())); } }
technique11 MapMarker { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(NoDepthDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,OverlayPS())); } }
technique11 MapArt { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(NoDepthDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,MapArtPS())); } }
technique11 Shadow { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,ShadowVS())); SetGeometryShader(NULL); SetPixelShader(NULL); } }
technique11 OccluderDepth { pass P0 { SetRasterizerState(OccluderRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(DepthOnlyBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,ShadowVS())); SetGeometryShader(NULL); SetPixelShader(NULL); } }
technique11 AlphaShadow { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,WorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,InstancedAlphaShadowPS())); } }
technique11 InstancedLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,InstancedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 InstancedLocalLightAdd { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AddBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,InstancedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LocalLightAddPS())); } }
technique11 InstancedAlphaTestLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,InstancedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 InstancedAlphaLit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthReadDSS,0); SetBlendState(AlphaBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,InstancedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,LitPS())); } }
technique11 InstancedUnlit { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,InstancedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,UnlitPS())); } }
technique11 InstancedWire { pass P0 { SetRasterizerState(WireRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,InstancedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,UnlitPS())); } }
technique11 InstancedShadow { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,InstancedWorldVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,InstancedAlphaShadowPS())); } }
technique11 DynamicDetail { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,DynamicDetailVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,DynamicDetailPS())); } }
technique11 DynamicDetailShadow { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(DepthWriteDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,DynamicDetailVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,DynamicDetailShadowPS())); } }
technique11 TemporalAA { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(NoDepthDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,PostVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,TemporalAAPS())); } }
technique11 FXAA { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(NoDepthDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,PostVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,FxaaPS())); } }
technique11 PostProcess { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(NoDepthDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,PostVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,PostPS())); } }
technique11 NameplateVisibility { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(NoDepthDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,NameplateVisibilityVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,NameplateVisibilityPS())); } }
technique11 ObjectVisibility { pass P0 { SetRasterizerState(SolidRS); SetDepthStencilState(NoDepthDSS,0); SetBlendState(OpaqueBS,float4(0,0,0,0),0xffffffff); SetVertexShader(CompileShader(vs_5_0,ObjectVisibilityVS())); SetGeometryShader(NULL); SetPixelShader(CompileShader(ps_5_0,ObjectVisibilityPS())); } }
