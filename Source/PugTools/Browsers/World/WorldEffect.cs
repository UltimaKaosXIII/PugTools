using System;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDXNet.FX;

namespace PugTools {
  internal sealed class WorldEffect : SlimDXNet.FX.Effect {
    public readonly EffectTechnique Lit, LocalLightAdd, AlphaTestLit, AlphaLit, AddLit, MultiplyLit, HookOverlay, HookAdditive, Unlit, Sky, TerrainCoverage, TerrainLit, TerrainAddLit, TerrainLocalLightAdd, TerrainUnlit, TerrainAddUnlit, Height, Wire, Water, Overlay, MapMarker, MapArt, Shadow, AlphaShadow, OccluderDepth, SkinnedLit, SkinnedLocalLightAdd, SkinnedAlphaTestLit, SkinnedAlphaLit, SkinnedAddLit, SkinnedMultiplyLit, SkinnedUnlit, SkinnedWire, InstancedLit, InstancedLocalLightAdd, InstancedAlphaTestLit, InstancedAlphaLit, InstancedUnlit, InstancedWire, InstancedShadow, DynamicDetail, DynamicDetailShadow, TemporalAA, Fxaa, PostProcess, NameplateVisibility, ObjectVisibility;
    private readonly EffectMatrixVariable world, worldInvTranspose, viewProj, shadow0, shadow1, shadow2, shadow3, localProjectorInv, taaReprojection, skinPalette;
    private readonly EffectVectorVariable camera, pointLight, frontLight, ambient, advanced, fog0, fog1, fogSky, fogParams, fogColorParams, shadowSplits, heightRange, overlay, materialFlatColor, materialBloomParams, opacityFadeParams, scrollOffset, scrollVisual, terrainChunkScale, materialUvScale, envBlend1, envBlend2, dydCameraRight, dydCreationDistances, dydParams, dydTextureInfo;
    private readonly EffectVectorVariable localPosRange, localColorIntensity, localDirType, localProjectorParams;
    private readonly EffectVectorVariable waterDeep, waterShallow, waterGloss, waterNmOffsets, waterParams, waterParams2, waterParams3, waterAlpha1, waterAlpha2, waterAlpha3, waterAlpha4, taaParams0, taaParams1, nameplateSamples, nameplateVisibilityParams, objectVisibilitySamples, objectVisibilityParams;
    private readonly EffectScalarVariable enableLighting, enableFog, enableShadows, localCount, alphaMode, alphaTest, diffuseAlphaMax, materialUsesEmissive, materialIsOpacityFade, hookOpacity, terrainUv, hasDiffuse, hasDiffuse2, hasRotation, hasGloss, hasTerrainMask, hasTerrainColor, hasIllumination, scrollingEnabled, hasEnvBlend, envBlendCrevice, envBlendTileMult, timeSeconds, hasColorLut, mapArtOpacity, nameplateDepthBias, viewerBlueGlow;
    private readonly EffectScalarVariable hasWaterNormal1, hasWaterNormal2, hasWaterDepth, hasWaterSurface, hasWaterEnvironment;
    private readonly EffectResourceVariable diffuse, diffuse2, rotation, gloss, terrainMask, terrainColor, illumination, scrollingTexture, scrollingMask, envBlendDiffuse, envBlendNormal, sm0, sm1, sm2, sm3, sceneColor, historyColor, sceneDepth, colorLut, mapArtTexture;
    private readonly EffectResourceVariable waterNormal1, waterNormal2, waterDepth, waterSurface, waterEnvironment;
    private readonly EffectResourceVariable[] localLightIllumination = new EffectResourceVariable[4];
    private readonly EffectResourceVariable[] localLightFalloff = new EffectResourceVariable[4];
    private readonly EffectResourceVariable[] localLightRamp = new EffectResourceVariable[4];

    public WorldEffect(Device device, string filename) : base(device, filename) {
      if (FX == null) throw new InvalidOperationException("World.fx could not be compiled. Fix the shader compiler error shown immediately before this message.");
      Lit=FX.GetTechniqueByName("Lit"); LocalLightAdd=FX.GetTechniqueByName("LocalLightAdd"); AlphaTestLit=FX.GetTechniqueByName("AlphaTestLit"); AlphaLit=FX.GetTechniqueByName("AlphaLit"); AddLit=FX.GetTechniqueByName("AddLit"); MultiplyLit=FX.GetTechniqueByName("MultiplyLit"); HookOverlay=FX.GetTechniqueByName("HookOverlay"); HookAdditive=FX.GetTechniqueByName("HookAdditive"); Unlit=FX.GetTechniqueByName("Unlit"); Sky=FX.GetTechniqueByName("Sky");
      TerrainCoverage=FX.GetTechniqueByName("TerrainCoverage"); TerrainLit=FX.GetTechniqueByName("TerrainLit"); TerrainAddLit=FX.GetTechniqueByName("TerrainAddLit"); TerrainLocalLightAdd=FX.GetTechniqueByName("TerrainLocalLightAdd"); TerrainUnlit=FX.GetTechniqueByName("TerrainUnlit"); TerrainAddUnlit=FX.GetTechniqueByName("TerrainAddUnlit");
      Height=FX.GetTechniqueByName("Height"); Wire=FX.GetTechniqueByName("Wire"); Water=FX.GetTechniqueByName("Water"); Overlay=FX.GetTechniqueByName("Overlay"); MapMarker=FX.GetTechniqueByName("MapMarker"); MapArt=FX.GetTechniqueByName("MapArt"); Shadow=FX.GetTechniqueByName("Shadow"); AlphaShadow=FX.GetTechniqueByName("AlphaShadow"); OccluderDepth=FX.GetTechniqueByName("OccluderDepth");
      SkinnedLit=FX.GetTechniqueByName("SkinnedLit"); SkinnedLocalLightAdd=FX.GetTechniqueByName("SkinnedLocalLightAdd"); SkinnedAlphaTestLit=FX.GetTechniqueByName("SkinnedAlphaTestLit"); SkinnedAlphaLit=FX.GetTechniqueByName("SkinnedAlphaLit"); SkinnedAddLit=FX.GetTechniqueByName("SkinnedAddLit"); SkinnedMultiplyLit=FX.GetTechniqueByName("SkinnedMultiplyLit"); SkinnedUnlit=FX.GetTechniqueByName("SkinnedUnlit"); SkinnedWire=FX.GetTechniqueByName("SkinnedWire");
      InstancedLit=FX.GetTechniqueByName("InstancedLit"); InstancedLocalLightAdd=FX.GetTechniqueByName("InstancedLocalLightAdd"); InstancedAlphaTestLit=FX.GetTechniqueByName("InstancedAlphaTestLit"); InstancedAlphaLit=FX.GetTechniqueByName("InstancedAlphaLit"); InstancedUnlit=FX.GetTechniqueByName("InstancedUnlit"); InstancedWire=FX.GetTechniqueByName("InstancedWire"); InstancedShadow=FX.GetTechniqueByName("InstancedShadow");
      DynamicDetail=FX.GetTechniqueByName("DynamicDetail"); DynamicDetailShadow=FX.GetTechniqueByName("DynamicDetailShadow"); TemporalAA=FX.GetTechniqueByName("TemporalAA"); Fxaa=FX.GetTechniqueByName("FXAA"); PostProcess=FX.GetTechniqueByName("PostProcess"); NameplateVisibility=FX.GetTechniqueByName("NameplateVisibility"); ObjectVisibility=FX.GetTechniqueByName("ObjectVisibility");
      world=FX.GetVariableByName("World").AsMatrix(); worldInvTranspose=FX.GetVariableByName("WorldInvTranspose").AsMatrix(); viewProj=FX.GetVariableByName("ViewProj").AsMatrix(); skinPalette=FX.GetVariableByName("SkinPalette").AsMatrix();
      shadow0=FX.GetVariableByName("ShadowMatrix0").AsMatrix(); shadow1=FX.GetVariableByName("ShadowMatrix1").AsMatrix(); shadow2=FX.GetVariableByName("ShadowMatrix2").AsMatrix(); shadow3=FX.GetVariableByName("ShadowMatrix3").AsMatrix();
      localProjectorInv=FX.GetVariableByName("LocalLightProjectorInv").AsMatrix(); taaReprojection=FX.GetVariableByName("TaaReprojection").AsMatrix();
      camera=FX.GetVariableByName("CameraPosition").AsVector(); pointLight=FX.GetVariableByName("PointLightDir").AsVector(); frontLight=FX.GetVariableByName("FrontLightColor").AsVector(); ambient=FX.GetVariableByName("AmbientLightColor").AsVector(); advanced=FX.GetVariableByName("AdvancedLightingParams").AsVector();
      fog0=FX.GetVariableByName("FogColor0").AsVector(); fog1=FX.GetVariableByName("FogColor1").AsVector(); fogSky=FX.GetVariableByName("FogColorSky").AsVector(); fogParams=FX.GetVariableByName("FogParams").AsVector(); fogColorParams=FX.GetVariableByName("FogColorParams").AsVector(); shadowSplits=FX.GetVariableByName("ShadowSplits").AsVector();
      heightRange=FX.GetVariableByName("HeightRange").AsVector(); overlay=FX.GetVariableByName("OverlayColor").AsVector(); materialFlatColor=FX.GetVariableByName("MaterialFlatColor").AsVector(); materialBloomParams=FX.GetVariableByName("MaterialBloomParams").AsVector(); opacityFadeParams=FX.GetVariableByName("OpacityFadeParams").AsVector(); scrollOffset=FX.GetVariableByName("ScrollingOffsetParams").AsVector(); scrollVisual=FX.GetVariableByName("ScrollingVisualParams").AsVector(); terrainChunkScale=FX.GetVariableByName("TerrainChunkScale").AsVector(); materialUvScale=FX.GetVariableByName("MaterialUvScale").AsVector(); envBlend1=FX.GetVariableByName("EnvBlendParams1").AsVector(); envBlend2=FX.GetVariableByName("EnvBlendParams2").AsVector();
      localPosRange=FX.GetVariableByName("LocalLightPosRange").AsVector(); localColorIntensity=FX.GetVariableByName("LocalLightColorIntensity").AsVector(); localDirType=FX.GetVariableByName("LocalLightDirType").AsVector(); localProjectorParams=FX.GetVariableByName("LocalLightProjectorParams").AsVector();
      dydCameraRight=FX.GetVariableByName("DydCameraRight").AsVector(); dydCreationDistances=FX.GetVariableByName("DydCreationDistances").AsVector(); dydParams=FX.GetVariableByName("DydParams").AsVector(); dydTextureInfo=FX.GetVariableByName("DydTextureInfo").AsVector();
      waterDeep=FX.GetVariableByName("WaterDeepColor").AsVector(); waterShallow=FX.GetVariableByName("WaterShallowColor").AsVector(); waterGloss=FX.GetVariableByName("WaterGlossColor").AsVector(); waterNmOffsets=FX.GetVariableByName("WaterNmOffsets").AsVector(); waterParams=FX.GetVariableByName("WaterParams").AsVector(); waterParams2=FX.GetVariableByName("WaterParams2").AsVector(); waterParams3=FX.GetVariableByName("WaterParams3").AsVector();
      waterAlpha1=FX.GetVariableByName("WaterAlphaParams1").AsVector(); waterAlpha2=FX.GetVariableByName("WaterAlphaParams2").AsVector(); waterAlpha3=FX.GetVariableByName("WaterAlphaParams3").AsVector(); waterAlpha4=FX.GetVariableByName("WaterAlphaParams4").AsVector(); taaParams0=FX.GetVariableByName("TaaParams0").AsVector(); taaParams1=FX.GetVariableByName("TaaParams1").AsVector(); nameplateSamples=FX.GetVariableByName("NameplateSamples").AsVector(); nameplateVisibilityParams=FX.GetVariableByName("NameplateVisibilityParams").AsVector(); objectVisibilitySamples=FX.GetVariableByName("ObjectVisibilitySamples").AsVector(); objectVisibilityParams=FX.GetVariableByName("ObjectVisibilityParams").AsVector();
      enableLighting=FX.GetVariableByName("EnableLighting").AsScalar(); enableFog=FX.GetVariableByName("EnableFog").AsScalar(); enableShadows=FX.GetVariableByName("EnableShadows").AsScalar(); localCount=FX.GetVariableByName("LocalLightCount").AsScalar(); alphaMode=FX.GetVariableByName("AlphaMode").AsScalar(); alphaTest=FX.GetVariableByName("AlphaTestValue").AsScalar(); diffuseAlphaMax=FX.GetVariableByName("DiffuseTextureAlphaMax").AsScalar(); materialUsesEmissive=FX.GetVariableByName("MaterialUsesEmissive").AsScalar(); materialIsOpacityFade=FX.GetVariableByName("MaterialIsOpacityFade").AsScalar(); hookOpacity=FX.GetVariableByName("HookOpacity").AsScalar(); terrainUv=FX.GetVariableByName("TerrainUvScale").AsScalar(); nameplateDepthBias=FX.GetVariableByName("NameplateDepthBias").AsScalar(); viewerBlueGlow=FX.GetVariableByName("ViewerBlueGlow").AsScalar();
      hasDiffuse=FX.GetVariableByName("HasDiffuse").AsScalar(); hasDiffuse2=FX.GetVariableByName("HasDiffuse2").AsScalar(); hasRotation=FX.GetVariableByName("HasRotation").AsScalar(); hasGloss=FX.GetVariableByName("HasGloss").AsScalar(); hasTerrainMask=FX.GetVariableByName("HasTerrainMask").AsScalar(); hasTerrainColor=FX.GetVariableByName("HasTerrainColor").AsScalar(); hasIllumination=FX.GetVariableByName("HasIllumination").AsScalar(); scrollingEnabled=FX.GetVariableByName("ScrollingEnabled").AsScalar(); hasEnvBlend=FX.GetVariableByName("HasEnvBlend").AsScalar(); envBlendCrevice=FX.GetVariableByName("EnvBlendEnableCreviceMap").AsScalar(); envBlendTileMult=FX.GetVariableByName("EnvBlendMaterialTileMult").AsScalar(); timeSeconds=FX.GetVariableByName("TimeSeconds").AsScalar(); hasColorLut=FX.GetVariableByName("HasColorLut").AsScalar(); mapArtOpacity=FX.GetVariableByName("MapArtOpacity").AsScalar();
      hasWaterNormal1=FX.GetVariableByName("HasWaterNormal1").AsScalar(); hasWaterNormal2=FX.GetVariableByName("HasWaterNormal2").AsScalar(); hasWaterDepth=FX.GetVariableByName("HasWaterDepth").AsScalar(); hasWaterSurface=FX.GetVariableByName("HasWaterSurface").AsScalar(); hasWaterEnvironment=FX.GetVariableByName("HasWaterEnvironment").AsScalar();
      diffuse=FX.GetVariableByName("DiffuseMap").AsResource(); diffuse2=FX.GetVariableByName("DiffuseMap2").AsResource(); rotation=FX.GetVariableByName("RotationMap").AsResource(); gloss=FX.GetVariableByName("GlossMap").AsResource(); terrainMask=FX.GetVariableByName("TerrainMask").AsResource(); terrainColor=FX.GetVariableByName("TerrainColorMap").AsResource(); illumination=FX.GetVariableByName("IlluminationMap").AsResource(); scrollingTexture=FX.GetVariableByName("ScrollingTextureMap").AsResource(); scrollingMask=FX.GetVariableByName("ScrollingMaskMap").AsResource(); envBlendDiffuse=FX.GetVariableByName("EnvBlendDiffuseMap").AsResource(); envBlendNormal=FX.GetVariableByName("EnvBlendNormalMap").AsResource();
      sm0=FX.GetVariableByName("ShadowMap0").AsResource(); sm1=FX.GetVariableByName("ShadowMap1").AsResource(); sm2=FX.GetVariableByName("ShadowMap2").AsResource(); sm3=FX.GetVariableByName("ShadowMap3").AsResource(); sceneColor=FX.GetVariableByName("SceneColorMap").AsResource(); historyColor=FX.GetVariableByName("HistoryColorMap").AsResource(); sceneDepth=FX.GetVariableByName("SceneDepthMap").AsResource(); colorLut=FX.GetVariableByName("ColorLut").AsResource(); mapArtTexture=FX.GetVariableByName("MapArtMap").AsResource();
      waterNormal1=FX.GetVariableByName("WaterNormalMap1").AsResource(); waterNormal2=FX.GetVariableByName("WaterNormalMap2").AsResource(); waterDepth=FX.GetVariableByName("WaterDepthMap").AsResource(); waterSurface=FX.GetVariableByName("WaterSurfaceMap").AsResource(); waterEnvironment=FX.GetVariableByName("WaterEnvironmentMap").AsResource();
      for(int n=0;n<4;n++){
        localLightIllumination[n]=FX.GetVariableByName("LocalLightIlluminationMap"+n).AsResource();
        localLightFalloff[n]=FX.GetVariableByName("LocalLightFalloffMap"+n).AsResource();
        localLightRamp[n]=FX.GetVariableByName("LocalLightRampMap"+n).AsResource();
      }
      ClearEnvironmentBlend(); ClearWater(); ClearMapArt(); ClearLocalLights(); ClearAntiAliasing();
    }

    public void SetWorld(Matrix m) {
      world.SetMatrix(m); Matrix inv; Matrix.Invert(ref m, out inv); Matrix.Transpose(ref inv, out inv); worldInvTranspose.SetMatrix(inv);
    }
    public void SetViewProj(Matrix m) => viewProj.SetMatrix(m);
    public void SetSkinPalette(Matrix[] matrices) { if(matrices!=null&&matrices.Length>0)skinPalette.SetMatrixArray(matrices); }
    public void SetCamera(Vector3 p) => camera.Set(new Vector4(p,1));
    public void SetEnvironment(AreaEnvironmentScheme e, bool lighting, bool fog, bool shadows, float viewDistanceScale = 3f) {
      e ??= new AreaEnvironmentScheme(); pointLight.Set(e.PointLightDirection); frontLight.Set(e.DiffuseLightColor); ambient.Set(e.AmbientLightColor); advanced.Set(e.AdvancedLightingParams);
      // Jedipedia deliberately scales the authored fog start/ramp and two-colour transition distances
      // with its viewer distance setting (default 3x). The amount coefficients x/y stay untouched.
      // Without this, SWTOR's authored ~25/100-unit fog reads as an extremely short draw distance.
      float scale=Math.Max(1f,Math.Min(10f,viewDistanceScale));
      Vector4 fa=e.FogAttenuation; fa.Z*=scale; fa.W*=scale;
      Vector2 fc=e.FogColorAttenuation; fc.X*=scale; fc.Y*=scale;
      fog0.Set(e.FogColor0); fog1.Set(e.FogColor1); fogSky.Set(e.FogColorSky); fogParams.Set(fa); fogColorParams.Set(new Vector4(fc.X,fc.Y,e.IsTwoColorFog?1:0,0));
      enableLighting.Set(lighting); enableFog.Set(fog); enableShadows.Set(shadows && e.CastDirectionalShadows);
    }
    public void SetMaterial(GR2_Material m) {
      diffuse.SetResource(m?.diffuseSRV); diffuse2.SetResource(m?.diffuse2SRV); rotation.SetResource(m?.rotationSRV); gloss.SetResource(m?.glossSRV); hasDiffuse.Set(m?.diffuseSRV!=null); hasDiffuse2.Set(m?.diffuse2SRV!=null); hasRotation.Set(m?.rotationSRV!=null); hasGloss.Set(m?.glossSRV!=null);
      hasTerrainMask.Set(false); hasTerrainColor.Set(false); alphaMode.Set(Alpha(m?.alphaMode)); alphaTest.Set(m?.alphaTestValue ?? 0); diffuseAlphaMax.Set(m?.diffuseTextureAlphaMax ?? 1f);
      materialFlatColor.Set(m?.diffuseFlatColor ?? new Vector4(1f,1f,1f,1f)); materialBloomParams.Set(m?.bloomMaterialParams ?? new Vector4(1f,0f,0f,0f));
      string derived=m?.derived ?? String.Empty;
      bool opacityFade=derived.Equals("OpacityFade",StringComparison.OrdinalIgnoreCase); materialIsOpacityFade.Set(opacityFade ? 1 : 0);
      opacityFadeParams.Set(new Vector4(m?.opacityFadeMinDistance ?? 0f,m?.opacityFadeMinOpacity ?? 0f,m?.opacityFadeMaxDistance ?? 100f,m?.opacityFadeMaxOpacity ?? 1f));
      bool selfLit=m?.useEmissive == true || derived.Equals("EmissiveOnly",StringComparison.OrdinalIgnoreCase) || derived.StartsWith("AnimatedUV",StringComparison.OrdinalIgnoreCase) || derived.StartsWith("AnimatedVFX",StringComparison.OrdinalIgnoreCase) || derived.StartsWith("AnimatedFlipbook",StringComparison.OrdinalIgnoreCase); materialUsesEmissive.Set(selfLit ? 1 : 0);
      Vector2 uv=m?.uvScale ?? new Vector2(1,1); materialUvScale.Set(new Vector4(uv.X,uv.Y,0,0)); ClearEnvironmentBlend();
    }
    public void SetMaterialFlatColor(Vector4 c) { materialFlatColor.Set(c); }
    public void SetHookOpacity(float value) { hookOpacity.Set(Math.Max(0f, Math.Min(1f, value))); }
    public void SetTerrain(GR2_Material m, ShaderResourceView mask, ShaderResourceView color, float uvScale, Vector2 chunkScale) {
      SetMaterial(m); terrainMask.SetResource(mask); terrainColor.SetResource(color); hasTerrainMask.Set(mask!=null); hasTerrainColor.Set(color!=null); terrainUv.Set(uvScale); terrainChunkScale.Set(new Vector4(chunkScale.X,chunkScale.Y,0,0));
    }
    public void SetEnvironmentBlend(AreaEnvironmentMaterial e, GR2_Material material, ShaderResourceView blendDiffuse, ShaderResourceView blendNormal) {
      bool on=e!=null && blendDiffuse!=null && blendNormal!=null;
      hasEnvBlend.Set(on); envBlendDiffuse.SetResource(on?blendDiffuse:null); envBlendNormal.SetResource(on?blendNormal:null);
      if(on){envBlend1.Set(e.EnvBlendParams1);envBlend2.Set(e.EnvBlendParams2);envBlendCrevice.Set(e.EnableCreviceMap);envBlendTileMult.Set(material?.envBlendMaterialTileMult ?? 1f);} else {envBlendCrevice.Set(false);envBlendTileMult.Set(1f);}
    }
    public void ClearEnvironmentBlend(){hasEnvBlend?.Set(false);envBlendDiffuse?.SetResource(null);envBlendNormal?.SetResource(null);envBlendCrevice?.Set(false);envBlendTileMult?.Set(1f);}
    public void SetIllumination(ShaderResourceView v) { illumination.SetResource(v); hasIllumination.Set(v!=null); }
    public void SetScrolling(AreaEnvironmentScheme e, float time, ShaderResourceView tex, ShaderResourceView mask) {
      bool on=e!=null && e.ScrollingLightEnabled && tex!=null && mask!=null; scrollingEnabled.Set(on); scrollingTexture.SetResource(tex); scrollingMask.SetResource(mask); timeSeconds.Set(time);
      if(on){ scrollOffset.Set(new Vector4(e.ScrollingSpeeds.X*time,e.ScrollingSpeeds.Y*time,e.ScrollingSpeeds.Z*time,e.ScrollingSpeeds.W*time)); scrollVisual.Set(e.ScrollingVisualParams); }
    }
    public void SetShadows(Matrix[] matrices, ShaderResourceView[] maps, float[] splits, bool enabled) {
      if(matrices!=null && matrices.Length>=4){shadow0.SetMatrix(matrices[0]);shadow1.SetMatrix(matrices[1]);shadow2.SetMatrix(matrices[2]);shadow3.SetMatrix(matrices[3]);}
      sm0.SetResource(enabled&&maps!=null&&maps.Length>0?maps[0]:null);sm1.SetResource(enabled&&maps!=null&&maps.Length>1?maps[1]:null);sm2.SetResource(enabled&&maps!=null&&maps.Length>2?maps[2]:null);sm3.SetResource(enabled&&maps!=null&&maps.Length>3?maps[3]:null);
      if(splits!=null&&splits.Length>=4)shadowSplits.Set(new Vector4(splits[0],splits[1],splits[2],splits[3]));
    }
    public void SetLocalLights(Vector4[] posRange, Vector4[] colors, Vector4[] dirs, Matrix[] projectorInv, Vector4[] projectorParams, ShaderResourceView[] illuminationMaps, ShaderResourceView[] falloffMaps, ShaderResourceView[] rampMaps, int count) {
      count=Math.Max(0,Math.Min(4,count)); localCount.Set(count);
      if(count>0){
        localPosRange.Set(posRange);localColorIntensity.Set(colors);localDirType.Set(dirs);
        if(projectorInv!=null&&projectorInv.Length>=count)localProjectorInv.SetMatrixArray(projectorInv);
        if(projectorParams!=null&&projectorParams.Length>=count)localProjectorParams.Set(projectorParams);
      }
      for(int n=0;n<4;n++){
        localLightIllumination[n].SetResource(illuminationMaps!=null && n<illuminationMaps.Length ? illuminationMaps[n] : null);
        localLightFalloff[n].SetResource(falloffMaps!=null && n<falloffMaps.Length ? falloffMaps[n] : null);
        localLightRamp[n].SetResource(rampMaps!=null && n<rampMaps.Length ? rampMaps[n] : null);
      }
    }
    public void SetLocalLights(Vector4[] posRange, Vector4[] colors, Vector4[] dirs, int count) => SetLocalLights(posRange,colors,dirs,null,null,null,null,null,count);
    public void ClearLocalLights(){
      localCount?.Set(0);
      for(int n=0;n<4;n++){
        localLightIllumination[n]?.SetResource(null);
        localLightFalloff[n]?.SetResource(null);
        localLightRamp[n]?.SetResource(null);
      }
    }
    public void SetHeightRange(float min,float max)=>heightRange.Set(new Vector4(min,max,0,0));
    public void SetOverlay(Vector4 c)=>overlay.Set(c);
    public void SetPlaceableBlueGlow(bool enabled)=>viewerBlueGlow?.Set(enabled ? 1f : 0f);

    public void SetWater(AssetInstance i, float elapsedSeconds, ShaderResourceView normal1, ShaderResourceView normal2, ShaderResourceView depthMap, ShaderResourceView surfaceMap, ShaderResourceView environmentMap) {
      if (i == null) { ClearWater(); return; }
      float dir1=WaterAngle(i.WaterNormalMap1Dir), dir2=WaterAngle(i.WaterNormalMap2Dir);
      float rot1=WaterAngle(i.WaterNormalMap1Rotation), rot2=WaterAngle(i.WaterNormalMap2Rotation);
      // Legacy text WTR instances can author WorldCoordScale. Jedipedia applies it to all
      // water UV scales before both texture scrolling and shader parameter upload.
      float worldScale=Single.IsFinite(i.WaterWorldCoordScale)?i.WaterWorldCoordScale:1f;
      float s1=i.WaterNormalMap1CoordScale*worldScale, s2=i.WaterNormalMap2CoordScale*worldScale;
      float surfaceScale=i.WaterSurfaceMapCoordScale*worldScale;
      float v1x=(float)Math.Cos(dir1)*i.WaterNormalMap1Speed*s1, v1y=(float)Math.Sin(dir1)*i.WaterNormalMap1Speed*s1;
      float v2x=(float)Math.Cos(dir2)*i.WaterNormalMap2Speed*s2, v2y=(float)Math.Sin(dir2)*i.WaterNormalMap2Speed*s2;
      waterDeep.Set(i.WaterDeepColor); waterShallow.Set(i.WaterShallowColor); waterGloss.Set(i.WaterGlossColor);
      waterNmOffsets.Set(new Vector4(v1x*elapsedSeconds,v1y*elapsedSeconds,v2x*elapsedSeconds,v2y*elapsedSeconds));
      waterParams.Set(new Vector4(rot1,rot2,0,i.WaterDistanceOpacityScale));
      waterParams2.Set(new Vector4(i.WaterAngleOpacityScale,i.WaterNormalMapScale,i.WaterDepthModulator,i.WaterSurfaceMapShininess));
      waterParams3.Set(new Vector4(s1,s2,surfaceScale,i.WaterReflectionModulator));
      waterAlpha1.Set(new Vector4(i.WaterKneePosition1,i.WaterKneePosition2,i.WaterKneePosition3,i.WaterSpecularPower));
      waterAlpha2.Set(new Vector4(i.WaterKneeValue1,i.WaterKneeValue2,i.WaterKneeValue3,i.WaterKneeValueAt1));
      waterAlpha3.Set(new Vector4(i.WaterSurfaceKneePosition1,i.WaterSurfaceKneePosition2,i.WaterSurfaceKneePosition3,i.WaterSurfaceKneeValueAt0));
      waterAlpha4.Set(new Vector4(i.WaterSurfaceKneeValue1,i.WaterSurfaceKneeValue2,i.WaterSurfaceKneeValue3,i.WaterSurfaceKneeValueAt1));
      waterNormal1.SetResource(normal1); waterNormal2.SetResource(normal2); waterDepth.SetResource(depthMap); waterSurface.SetResource(surfaceMap); waterEnvironment.SetResource(environmentMap);
      hasWaterNormal1.Set(normal1!=null); hasWaterNormal2.Set(normal2!=null); hasWaterDepth.Set(depthMap!=null); hasWaterSurface.Set(surfaceMap!=null); hasWaterEnvironment.Set(environmentMap!=null);
    }
    public void ClearWater(){
      waterNormal1?.SetResource(null);waterNormal2?.SetResource(null);waterDepth?.SetResource(null);waterSurface?.SetResource(null);waterEnvironment?.SetResource(null);
      hasWaterNormal1?.Set(false);hasWaterNormal2?.Set(false);hasWaterDepth?.Set(false);hasWaterSurface?.Set(false);hasWaterEnvironment?.Set(false);
    }
    private static float WaterAngle(float value)=>Math.Abs(value)>Math.PI*2 ? value*(float)Math.PI/180f : value;

    public void SetDynamicDetail(Vector3 cameraRight, int atlasMode, float wind, float backlightStrength, Vector2 textureSize) {
      if(cameraRight.LengthSquared()<.000001f)cameraRight=new Vector3(1,0,0);else cameraRight.Normalize();
      dydCameraRight.Set(new Vector4(cameraRight,0));
      dydCreationDistances.Set(new Vector4(3.5f,4.4f,6f,8.1f));
      // x = fifth creation band, y = wind amplitude, z = atlas mode, w reserved.
      dydParams.Set(new Vector4(10f,wind,Math.Max(0,Math.Min(2,atlasMode)),backlightStrength));
      dydTextureInfo.Set(new Vector4(Math.Max(1,textureSize.X),Math.Max(1,textureSize.Y),0,0));
    }

    public void SetMapArt(ShaderResourceView texture,float opacity){mapArtTexture.SetResource(texture);mapArtOpacity.Set(Math.Max(0f,Math.Min(1f,opacity)));}
    public void ClearMapArt(){mapArtTexture?.SetResource(null);mapArtOpacity?.Set(0f);}

    public void SetTemporalAA(ShaderResourceView scene, ShaderResourceView history, ShaderResourceView depth, Matrix reprojection, Vector2 texelSize, Vector2 jitterPixels, float historyWeight, bool hasHistory){
      sceneColor.SetResource(scene); historyColor.SetResource(history); sceneDepth.SetResource(depth); taaReprojection.SetMatrix(reprojection);
      taaParams0.Set(new Vector4(texelSize.X,texelSize.Y,jitterPixels.X,jitterPixels.Y)); taaParams1.Set(new Vector4(historyWeight,hasHistory?1f:0f,0,0));
    }
    public void SetFxaa(ShaderResourceView scene, Vector2 texelSize){sceneColor.SetResource(scene);taaParams0.Set(new Vector4(texelSize.X,texelSize.Y,0,0));}
    public void SetNameplateVisibility(ShaderResourceView depth, Vector4[] samples, int count, int viewportWidth, int viewportHeight, float depthBias) {
      if (samples == null || count <= 0) return;
      sceneDepth.SetResource(depth);
      nameplateSamples.Set(samples);
      float width=Math.Max(1,viewportWidth),height=Math.Max(1,viewportHeight);
      nameplateVisibilityParams.Set(new Vector4(width,height,1f/width,1f/height));
      nameplateDepthBias.Set(depthBias);
    }
    public void ClearNameplateVisibility(){sceneDepth?.SetResource(null);}
    public void SetObjectVisibility(ShaderResourceView depth, Vector4[] samples, int count, int outputWidth, int viewportWidth, int viewportHeight, float depthBias) {
      if (samples == null || count <= 0) return;
      sceneDepth.SetResource(depth);
      objectVisibilitySamples.Set(samples);
      objectVisibilityParams.Set(new Vector4(Math.Max(1,outputWidth),Math.Max(1,viewportWidth),Math.Max(1,viewportHeight),depthBias));
    }
    public void ClearObjectVisibility(){sceneDepth?.SetResource(null);}
    public void ClearAntiAliasing(){historyColor?.SetResource(null);sceneDepth?.SetResource(null);}
    public void SetPost(ShaderResourceView scene, ShaderResourceView lut){sceneColor.SetResource(scene);colorLut.SetResource(lut);hasColorLut.Set(lut!=null);}
    public void ClearPost(){sceneColor.SetResource(null);colorLut.SetResource(null);hasColorLut.Set(false);}
    private static int Alpha(string s){if(string.IsNullOrEmpty(s))return 0;if(s.Equals("Test",StringComparison.OrdinalIgnoreCase))return 1;if(s.Equals("Add",StringComparison.OrdinalIgnoreCase))return 2;if(s.Equals("Multiply",StringComparison.OrdinalIgnoreCase))return 3;if(s.Equals("Full",StringComparison.OrdinalIgnoreCase)||s.Equals("MultiPassFull",StringComparison.OrdinalIgnoreCase))return 4;return 0;}
  }
}
