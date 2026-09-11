using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Xml;
using GomLib;
using SlimDX;
using SlimDX.Direct3D11;
using TorArchive;
using File = TorArchive.File;
using ShaderResourceView = SlimDX.Direct3D11.ShaderResourceView;

namespace FileFormats {
  public class GR2_Material {
    public String ageDDS;
    public ShaderResourceView ageSRV;
    public Boolean alphaClip;
    public String alphaMode;
    public Single alphaTestValue;
    public String complexionDDS;
    public ShaderResourceView complexionSRV;
    public String derived;
    public String diffuseDDS;
    public ShaderResourceView diffuseSRV;
    // Optional low-frequency/selector map used by SWTOR TerrainAntiTile.
    public String diffuse2DDS;
    public ShaderResourceView diffuse2SRV;
    public String facepaintDDS;
    public ShaderResourceView facepaintSRV;
    public Single fleshBrightness;
    public Vector4 flushTone;
    // private Vector4 glassParams;
    public String glossDDS;
    public ShaderResourceView glossSRV;
    public Boolean isTwoSided;
    public String materialName;
    // The lookup key may be made unique for per-NPC palette variants, while this keeps the authored .mat name.
    public String sourceMaterialName;
    public Vector4 palette1;
    public Vector4 palette1MetSpec;
    public Vector4 palette1Spec;
    public String palette1XML;
    public Vector4 palette2;
    public Vector4 palette2MetSpec;
    public Vector4 palette2Spec;
    public String palette2XML;
    public String paletteDDS;
    public String paletteMaskDDS;
    public ShaderResourceView paletteMaskSRV;
    public ShaderResourceView paletteSRV;
    public Boolean parsed;
    // Runtime-generated materials (currently native SpeedTree geometry) have no backing .mat file.
    // Keep their resolved texture metadata alive when the world texture LRU evicts only the GPU SRVs.
    public Boolean runtimeGenerated;
    // SWTOR uses these authoring flags to keep editor/utility geometry out of the in-game render.
    public String polyType;
    public String visibility;
    public String rotationDDS;
    public ShaderResourceView rotationSRV;
    public Boolean useEmissive;
    // World-viewer material parameters also used by Jedipedia's Uber/UberEnvBlend paths.
    public Vector2 uvScale = new Vector2(1f, 1f);
    public Single envBlendMaterialTileMult = 1f;
    public Single diffuseTextureAlphaMax = 1f;
    // SWTOR OpacityFade: distance-driven opacity used for dark/light transition panels in doorways/windows.
    // Defaults match the game/Jedipedia shader when a MAT omits one of the inputs.
    public Single opacityFadeMinDistance = 0f;
    public Single opacityFadeMinOpacity = 0f;
    public Single opacityFadeMaxDistance = 100f;
    public Single opacityFadeMaxOpacity = 1f;
    // Material tint used by SWTOR VFX/stronghold hook shaders (diffuseFlatColorProp).
    public Vector4 diffuseFlatColor = new Vector4(1f, 1f, 1f, 1f);
    public Boolean hasDiffuseFlatColor;
    public Vector4 bloomMaterialParams = new Vector4(1f, 0f, 0f, 0f);
    // Grass/DynamicDetail lighting control authored by SWTOR materials. Jedipedia defaults to (1,1,1,0).
    public Vector4 vegetationParams2 = new Vector4(1f, 1f, 1f, 0f);
    // Volume texture used by SWTOR water materials. ShaderResourceView.FromStream also handles volume DDS files.
    public String waterSurfaceDDS;
    public ShaderResourceView waterSurfaceSRV;
    private Int32 textureFirstMipLevel;
    // private Boolean useReflection;
    // private String visibility;

    public GR2_Material(String materialName) {
      this.materialName = materialName;
      sourceMaterialName = materialName;
    }

    public GR2_Material(BinaryReader br, Boolean is64Bit) {
      UInt64 offsetMaterialName = is64Bit ? br.ReadUInt64() : br.ReadUInt32();
      materialName = FileHelpers.ReadString(br, offsetMaterialName);
      sourceMaterialName = materialName;
    }

    private static void FileToShaderResource(ref Device device,
                                             File file,
                                             ref ShaderResourceView srv,
                                             Int32 firstMipLevel = 0) {

      if (file != null && device != null) {
        using Stream textureStream = file.OpenCopyInMemory();
        Int32 mip = ClampDdsFirstMipLevel(textureStream, firstMipLevel);
        if (mip > 0) {
          ImageLoadInformation loadInfo = ImageLoadInformation.FromDefaults();
          loadInfo.FirstMipLevel = mip;
          srv = ShaderResourceView.FromStream(device, textureStream, (Int32)textureStream.Length, loadInfo);
        } else {
          srv = ShaderResourceView.FromStream(device, textureStream, (Int32)textureStream.Length);
        }
      } else {
        return;
      }
    }

    // D3DX maps FirstMipLevel to level 0 in the created resource. Clamp the requested skip to the
    // mip count stored in the DDS header so tiny/single-level utility textures still load normally.
    private static Int32 ClampDdsFirstMipLevel(Stream stream, Int32 requested) {
      if (requested <= 0 || stream == null || !stream.CanSeek) return 0;
      Int64 oldPosition = stream.Position;
      try {
        if (stream.Length < 32) return 0;
        Byte[] header = new Byte[32];
        stream.Position = 0;
        Int32 read = stream.Read(header, 0, header.Length);
        if (read < header.Length || BitConverter.ToUInt32(header, 0) != 0x20534444) return 0;
        UInt32 mipCount = BitConverter.ToUInt32(header, 28);
        if (mipCount == 0) mipCount = 1;
        return Math.Min(requested, Math.Max(0, (Int32)mipCount - 1));
      } catch {
        return 0;
      } finally {
        try { stream.Position = oldPosition; } catch { }
      }
    }

    private static void FileToShaderResource(ref Device device,
                                             String resourcePath,
                                             ref ShaderResourceView srv,
                                             Int32 firstMipLevel = 0) {

      if (device == null || String.IsNullOrWhiteSpace(resourcePath)) return;
      Assets curAssets = AssetHandler.Instance.GetCurrentAssets();
      if (curAssets == null) return;
      using File file = curAssets.FindFile(resourcePath);

      FileToShaderResource(ref device, file, ref srv, firstMipLevel);
    }

    private static void EnsureTextureResource(ref Device device, String resourcePath, ref ShaderResourceView srv, Int32 firstMipLevel) {
      if (srv != null || device == null || String.IsNullOrWhiteSpace(resourcePath)) return;
      FileToShaderResource(ref device, resourcePath, ref srv, firstMipLevel);
    }

    /// <summary>
    /// Recreates texture SRVs from paths already parsed from the MAT. Several PugTools viewers parse MAT metadata
    /// with a null D3D device; that correctly fills the material fields but intentionally cannot create GPU textures.
    /// The world viewer is streamed/lazy now, so a simple "parsed" check must not treat those metadata-only materials
    /// as GPU-ready. This method is intentionally idempotent and also repairs SRVs evicted by the world LRU.
    /// </summary>
    public void EnsureTextureResources(Device device, Int32 firstMipLevel = 0) {
      if (device == null) return;
      textureFirstMipLevel = Math.Max(0, firstMipLevel);
      EnsureTextureResource(ref device, diffuseDDS, ref diffuseSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, diffuse2DDS, ref diffuse2SRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, rotationDDS, ref rotationSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, glossDDS, ref glossSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, paletteDDS, ref paletteSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, paletteMaskDDS, ref paletteMaskSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, ageDDS, ref ageSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, complexionDDS, ref complexionSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, facepaintDDS, ref facepaintSRV, textureFirstMipLevel);
      EnsureTextureResource(ref device, waterSurfaceDDS, ref waterSurfaceSRV, textureFirstMipLevel);
    }

    /// <summary>
    /// SWTOR materials keep the authored texture in &lt;value&gt; and, for a
    /// number of shipped materials, the actually packed texture in
    /// &lt;variable&gt;. The game and Jedipedia fall back to &lt;variable&gt; when
    /// &lt;value&gt; does not resolve. PugTools historically ignored that field,
    /// which leaves otherwise valid materials grey/white (or on the generic
    /// fallback texture).
    /// </summary>
    private static String ResolveTextureResourcePath(
      Assets assets,
      String authoredValue,
      String variable
    ) {
      String primary = NormalizeTextureResourcePath(authoredValue);
      if (!String.IsNullOrWhiteSpace(primary)
          && TextureResourceExists(assets, primary))
        return primary;

      String fallback = NormalizeTextureResourcePath(variable);
      if (!String.IsNullOrWhiteSpace(fallback)
          && TextureResourceExists(assets, fallback))
        return fallback;

      // Preserve the authored path for diagnostics/callers even when neither
      // file exists. The caller decides whether to use a neutral fallback.
      return primary;
    }

    private static Boolean TextureResourceExists(Assets assets, String path) {
      if (assets == null || String.IsNullOrWhiteSpace(path)) return false;
      using File file = assets.FindFile(path);
      return file != null;
    }

    private static String NormalizeTextureResourcePath(String rawPath) {
      if (String.IsNullOrWhiteSpace(rawPath)) return null;

      String path = rawPath.Trim().Replace('\\', '/');
      while (path.Contains("//")) path = path.Replace("//", "/");

      if (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
        path = "/" + path;
      else if (!path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) {
        if (path.StartsWith("/")) path = path.Substring(1);
        path = "/resources/" + path;
      }

      if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        path += ".dds";

      return path.Replace("//", "/");
    }

    private static void LoadMaterialTexture(
      ref Device device,
      Assets assets,
      XmlNode inputNode,
      ref String ddsPath,
      ref ShaderResourceView srv,
      Boolean useBlueFallback = false,
      Int32 firstMipLevel = 0
    ) {
      String authoredValue = inputNode?["value"]?.InnerText;
      String variable = inputNode?["variable"]?.InnerText;
      String resolved = ResolveTextureResourcePath(assets, authoredValue, variable);

      if (!String.IsNullOrWhiteSpace(resolved)) {
        using File file = assets?.FindFile(resolved);
        if (file != null) {
          ddsPath = resolved;
          FileToShaderResource(ref device, file, ref srv, firstMipLevel);
          return;
        }
      }

      ddsPath = resolved;
      if (useBlueFallback) {
        ddsPath = "/resources/art/defaultassets/blue.dds";
        FileToShaderResource(ref device, ddsPath, ref srv, firstMipLevel);
      }
    }

    private static Vector2 ParseVector2(String text, Vector2 fallback) {
      if (String.IsNullOrWhiteSpace(text)) return fallback;
      String[] parts = text.Trim().Trim('(', ')', '[', ']').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 1 && Single.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out Single scalar))
        return new Vector2(scalar, scalar);
      if (parts.Length >= 2
          && Single.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out Single x)
          && Single.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out Single y))
        return new Vector2(x, y);
      return fallback;
    }

    private static Single ParseSingle(String text, Single fallback) {
      return Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out Single parsed) ? parsed : fallback;
    }

    private static Vector4 ParseVector4(String text, Vector4 fallback) {
      if (String.IsNullOrWhiteSpace(text)) return fallback;
      String[] parts = text.Trim().Trim('(', ')', '[', ']').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length >= 4
          && Single.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out Single x)
          && Single.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out Single y)
          && Single.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out Single z)
          && Single.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out Single w))
        return new Vector4(x, y, z, w);
      return fallback;
    }

    public void ParseMAT(Device device, List<GR2_Material> parentMaterials = null, Int32 firstMipLevel = 0) {
      textureFirstMipLevel = Math.Max(0, firstMipLevel);
      String materialFileName = "/resources/art/shaders/materials/" + (String.IsNullOrWhiteSpace(sourceMaterialName) ? materialName : sourceMaterialName) + ".mat";
      Assets currentAssets = AssetHandler.Instance.GetCurrentAssets();

      try {
        if (palette1XML != null) {
          File palette1File = currentAssets.FindFile(palette1XML);

          if (palette1File != null) {
            using Stream palette1Stream = palette1File.OpenCopyInMemory();
            XmlDocument p1XmlDoc = new XmlDocument();
            p1XmlDoc.Load(palette1Stream);

            Vector4 metSpec = FileHelpers.StringToVec4(
              p1XmlDoc.DocumentElement.SelectSingleNode("/Palette/Metallicspecular").InnerText
            );
            Vector4 spec = FileHelpers.StringToVec4(
              p1XmlDoc.DocumentElement.SelectSingleNode("/Palette/Specular").InnerText
            );
            Single hue = ParseSingle(p1XmlDoc.DocumentElement.SelectSingleNode("/Palette/Hue")?.InnerText, 0f);
            Single bright = ParseSingle(p1XmlDoc.DocumentElement.SelectSingleNode("/Palette/Brightness")?.InnerText, 0f);
            Single saturation = ParseSingle(p1XmlDoc.DocumentElement.SelectSingleNode("/Palette/Saturation")?.InnerText, .5f);
            Single contrast = ParseSingle(p1XmlDoc.DocumentElement.SelectSingleNode("/Palette/Contrast")?.InnerText, 1f);

            palette1 = new Vector4(hue, saturation, bright, contrast);
            palette1MetSpec = metSpec;
            palette1Spec = spec;

            palette1File.Dispose();
          }
        }

        if (palette2XML != null) {
          File palette2File = currentAssets.FindFile(palette2XML);
          if (palette2File != null) {
            using Stream palette2Stream = palette2File.OpenCopyInMemory();
            XmlDocument p2XmlDoc = new XmlDocument();
            p2XmlDoc.Load(palette2Stream);

            Vector4 metSpec = FileHelpers.StringToVec4(
              p2XmlDoc.DocumentElement.SelectSingleNode("/Palette/Metallicspecular").InnerText
            );
            Vector4 spec = FileHelpers.StringToVec4(
              p2XmlDoc.DocumentElement.SelectSingleNode("/Palette/Specular").InnerText
            );
            Single hue = ParseSingle(p2XmlDoc.DocumentElement.SelectSingleNode("/Palette/Hue")?.InnerText, 0f);
            Single bright = ParseSingle(p2XmlDoc.DocumentElement.SelectSingleNode("/Palette/Brightness")?.InnerText, 0f);
            Single saturation = ParseSingle(p2XmlDoc.DocumentElement.SelectSingleNode("/Palette/Saturation")?.InnerText, .5f);
            Single contrast = ParseSingle(p2XmlDoc.DocumentElement.SelectSingleNode("/Palette/Contrast")?.InnerText, 1f);

            palette2 = new Vector4(hue, saturation, bright, contrast);
            palette2MetSpec = metSpec;
            palette2Spec = spec;

            palette2File.Dispose();
          }
        }
      }
      catch (Exception) { }

      File materialFile = currentAssets.FindFile(materialFileName);

      if (materialFile == null) {
        materialFile = currentAssets.FindFile(
          materialFileName.Replace("_m_", "_u_").Replace("_f_", "_u_")
        );

        // if (materialFile != null)
        //     materialFileName = materialFileName.Replace("_m_", "_u_").Replace("_f_", "_u_");
        // materialFile = currentAssets.FindFile(materialFileName.Replace("_u_", "_f_"));
        // if (materialFile != null)
        //     materialFileName = materialFileName.Replace("_u_", "_f_");
        // materialFile = currentAssets.FindFile(materialFileName.Replace("_u_", "_m_"));
        // if (materialFile != null)
        //     _ = materialFileName.Replace("_u_", "_m_");
      }

      if (materialFile != null) {
        using Stream materialStream = materialFile.OpenCopyInMemory();
        XmlDocument material = new XmlDocument();
        material.Load(materialStream);

        // MATs are asset data and always use invariant decimal syntax. Using the Windows UI culture here
        // (for example de-DE) could throw before the texture inputs were even visited, leaving terrain
        // materials completely untextured. Keep the parser deliberately tolerant like Jedipedia's readMat.
        derived = material.SelectSingleNode("/Material/Derived")?.InnerText ?? String.Empty;
        polyType = material.SelectSingleNode("/Material/PolyType")?.InnerText ?? String.Empty;
        visibility = material.SelectSingleNode("/Material/Visibility")?.InnerText ?? String.Empty;

        String alphaMode = material.SelectSingleNode("/Material/AlphaMode")?.InnerText ?? "None";
        String alphaTestValue = material.SelectSingleNode("/Material/AlphaTestValue")?.InnerText ?? "0";
        this.alphaMode = alphaMode;
        alphaClip = !alphaMode.Equals("None", StringComparison.OrdinalIgnoreCase);
        this.alphaTestValue = ParseSingle(alphaTestValue, 0f);
        if (this.alphaTestValue > 1f) this.alphaTestValue = Math.Min(1f, this.alphaTestValue / 255f);
        Boolean.TryParse(material.SelectSingleNode("/Material/IsTwoSided")?.InnerText, out isTwoSided);
        XmlNodeList nodeList = material.SelectNodes("/Material/input");

        foreach (XmlNode node in nodeList) {
          String semantic = node["semantic"]?.InnerText;
          if (String.IsNullOrWhiteSpace(semantic)) continue;
          String inputValue = node["value"]?.InnerText?.Replace("\\", "/") ?? String.Empty;

          if (semantic.Equals("DiffuseMap", StringComparison.OrdinalIgnoreCase)) {
            LoadMaterialTexture(
              ref device, currentAssets, node, ref diffuseDDS, ref diffuseSRV, true, firstMipLevel
            );
          } else if (semantic.Equals("DiffuseMap2", StringComparison.OrdinalIgnoreCase)) {
            // TerrainAntiTile uses this as both selector noise and distant macro albedo.
            // Do not bind a fake fallback here: the shader deliberately takes a seam-free
            // single-sample path when the material has no real DiffuseMap2.
            LoadMaterialTexture(
              ref device, currentAssets, node, ref diffuse2DDS, ref diffuse2SRV, false, firstMipLevel
            );
          } else if (semantic.Equals("RotationMap1", StringComparison.OrdinalIgnoreCase) || semantic.Equals("RotationMap", StringComparison.OrdinalIgnoreCase)) {
            LoadMaterialTexture(
              ref device, currentAssets, node, ref rotationDDS, ref rotationSRV, false, firstMipLevel
            );
          } else if (semantic.Equals("GlossMap", StringComparison.OrdinalIgnoreCase)) {
            LoadMaterialTexture(
              ref device, currentAssets, node, ref glossDDS, ref glossSRV, false, firstMipLevel
            );
          } else if (semantic.Equals("UsesEmissive", StringComparison.OrdinalIgnoreCase)) {
            Boolean.TryParse(inputValue, out useEmissive);
          } else if (semantic.Equals("UvScale", StringComparison.OrdinalIgnoreCase) || semantic.Equals("UvScaling", StringComparison.OrdinalIgnoreCase)) {
            uvScale = ParseVector2(inputValue, uvScale);
          } else if (semantic.Equals("envBlendMaterialTileMult", StringComparison.OrdinalIgnoreCase)) {
            envBlendMaterialTileMult = ParseSingle(inputValue, 1f);
          } else if (semantic.Equals("DiffuseTextureAlphaMax", StringComparison.OrdinalIgnoreCase)) {
            diffuseTextureAlphaMax = ParseSingle(inputValue, 1f);
          } else if (semantic.Equals("OpacityFadeMinDistance", StringComparison.OrdinalIgnoreCase)) {
            opacityFadeMinDistance = ParseSingle(inputValue, opacityFadeMinDistance);
          } else if (semantic.Equals("OpacityFadeMinOpacity", StringComparison.OrdinalIgnoreCase)) {
            opacityFadeMinOpacity = ParseSingle(inputValue, opacityFadeMinOpacity);
          } else if (semantic.Equals("OpacityFadeMaxDistance", StringComparison.OrdinalIgnoreCase)) {
            opacityFadeMaxDistance = ParseSingle(inputValue, opacityFadeMaxDistance);
          } else if (semantic.Equals("OpacityFadeMaxOpacity", StringComparison.OrdinalIgnoreCase)) {
            opacityFadeMaxOpacity = ParseSingle(inputValue, opacityFadeMaxOpacity);
          } else if (semantic.Equals("diffuseFlatColorProp", StringComparison.OrdinalIgnoreCase)) {
            diffuseFlatColor = ParseVector4(inputValue, new Vector4(1f,1f,1f,1f));
            hasDiffuseFlatColor = true;
          } else if (semantic.Equals("BloomMaterialParams", StringComparison.OrdinalIgnoreCase) || semantic.Equals("bloomMaterialParams", StringComparison.OrdinalIgnoreCase)) {
            bloomMaterialParams = ParseVector4(inputValue, new Vector4(1f,0f,0f,0f));
          } else if (semantic.Equals("vegetationParams2", StringComparison.OrdinalIgnoreCase)) {
            try { vegetationParams2 = FileHelpers.StringToVec4(inputValue); } catch { vegetationParams2 = new Vector4(1f,1f,1f,0f); }
          } else if (semantic.Equals("WaterSurfaceMap", StringComparison.OrdinalIgnoreCase)) {
            LoadMaterialTexture(
              ref device, currentAssets, node, ref waterSurfaceDDS, ref waterSurfaceSRV, false, firstMipLevel
            );
          }

          if (derived.Equals("Garment", StringComparison.OrdinalIgnoreCase) || derived.Equals("GarmentScrolling", StringComparison.OrdinalIgnoreCase) || derived.Equals("SkinB", StringComparison.OrdinalIgnoreCase)
              || derived.Equals("HairC", StringComparison.OrdinalIgnoreCase) || derived.Equals("Eye", StringComparison.OrdinalIgnoreCase)) {

            if (semantic == "PaletteMap") {
              LoadMaterialTexture(
                ref device, currentAssets, node, ref paletteDDS, ref paletteSRV, false, firstMipLevel
              );
            } else if (semantic == "PaletteMaskMap") {
              LoadMaterialTexture(
                ref device, currentAssets, node, ref paletteMaskDDS, ref paletteMaskSRV, false, firstMipLevel
              );
            } else if (semantic == "palette1") {
              if (palette1 == new Vector4())
                palette1 = FileHelpers.StringToVec4(inputValue);
            } else if (semantic == "palette2") {
              if (palette2 == new Vector4())
                palette2 = FileHelpers.StringToVec4(inputValue);
            } else if (semantic == "palette1Specular") {
              palette1Spec = FileHelpers.StringToVec4(inputValue);
            } else if (semantic == "palette2Specular") {
              palette2Spec = FileHelpers.StringToVec4(inputValue);
            } else if (semantic == "palette1MetallicSpecular") {
              palette1MetSpec = FileHelpers.StringToVec4(inputValue);
            } else if (semantic == "palette2MetallicSpecular") {
              palette2MetSpec = FileHelpers.StringToVec4(inputValue);
            }
          }

          if (derived.Equals("SkinB", StringComparison.OrdinalIgnoreCase)) {
            if (semantic == "ComplexionMap") {
              LoadMaterialTexture(
                ref device, currentAssets, node, ref complexionDDS, ref complexionSRV, false, firstMipLevel
              );
            } else if (semantic == "FacepaintMap") {
              LoadMaterialTexture(
                ref device, currentAssets, node, ref facepaintDDS, ref facepaintSRV, false, firstMipLevel
              );
            } else if (semantic == "AgeMap") {
              LoadMaterialTexture(
                ref device, currentAssets, node, ref ageDDS, ref ageSRV, false, firstMipLevel
              );
            } else if (semantic == "FlushTone") {
              if (flushTone == new Vector4())
                flushTone = FileHelpers.StringToVec4(inputValue);
            } else if (semantic == "FleshBrightness") {
              if (fleshBrightness == 0)
                fleshBrightness = ParseSingle(inputValue, fleshBrightness);
            }
          }

          /*
          if (derived == "Glass") {
            if (semantic == "UsesReflection") {
              useReflection = Convert.ToBoolean(inputValue);
            } else if (semantic == "GlassParams") {
              glassParams = FileHelpers.StringToVec4(inputValue);
            }
          }
          */
        }

        // OpacityFade is not an opaque black helper wall. The game replaces texture alpha with a distance ramp.
        // A non-zero cutoff with None/Test is the authored hard-cutout form; otherwise Jedipedia promotes the
        // material to Full so the ramp becomes real translucency (notably hut_env_interior_trans_dark on Hutta).
        if (derived.Equals("OpacityFade", StringComparison.OrdinalIgnoreCase)) {
          Boolean cutout = this.alphaTestValue > 0f &&
            (this.alphaMode.Equals("None", StringComparison.OrdinalIgnoreCase) || this.alphaMode.Equals("Test", StringComparison.OrdinalIgnoreCase));
          if (!cutout) this.alphaMode = "Full";
          alphaClip = cutout || !this.alphaMode.Equals("None", StringComparison.OrdinalIgnoreCase);
        }

        if (palette1.X == 0 && palette1.Y == 0.5 && palette1.Z == 0 && palette1.W == 1
            && parentMaterials != null) {

          if (parentMaterials[0] != null) {
            palette1 = parentMaterials[0].palette1;
            palette1MetSpec = parentMaterials[0].palette1MetSpec;
            palette1Spec = parentMaterials[0].palette1Spec;
            palette2 = parentMaterials[0].palette2;
            palette2MetSpec = parentMaterials[0].palette2MetSpec;
            palette2Spec = parentMaterials[0].palette2Spec;
          }
        }
      } else {
        diffuseDDS = "/resources/art/defaultassets/blue.dds";
        FileToShaderResource(ref device, diffuseDDS, ref diffuseSRV, firstMipLevel);
      }

      parsed = true;
    }

    public void SetComplexionMap(Device device, String complexionPath) {
      complexionDDS = "/resources" + complexionPath;
      FileToShaderResource(ref device, complexionDDS, ref complexionSRV, textureFirstMipLevel);
    }

    public void SetDynamicColor(GomObject dynObj, Int32 paletteNum = 0) {
      Single hue = dynObj.Data.ValueOrDefault<Single>("appPaletteHue", 0);
      Single saturation = dynObj.Data.ValueOrDefault("appPaletteSaturation", 0.5F);
      Single brightness = dynObj.Data.ValueOrDefault<Single>("appPaletteBrightness", 0);
      Single contrast = dynObj.Data.ValueOrDefault("appPaletteContrast", 1.0F);

      Vector4 palette = new Vector4(hue, saturation, brightness, contrast);
      GomObjectData specData = (GomObjectData)dynObj.Data.Dictionary["appPaletteSpecular"];
      Vector4 specular = new Vector4(
        (Single)specData.Dictionary["r"], (Single)specData.Dictionary["g"],
        (Single)specData.Dictionary["b"], (Single)specData.Dictionary["a"]
      );
      GomObjectData metSpecData =
        (GomObjectData)dynObj.Data.Dictionary["appPaletteMetallicSpecular"];
      Vector4 metallicSpecular = new Vector4(
        (Single)metSpecData.Dictionary["r"], (Single)metSpecData.Dictionary["g"],
        (Single)metSpecData.Dictionary["b"], (Single)metSpecData.Dictionary["a"]
      );

      if (paletteNum != 0) {
        if (paletteNum == 1) {
          palette1 = palette;
          palette1MetSpec = metallicSpecular;
          palette1Spec = specular;
        }

        if (paletteNum == 2) {
          palette2 = palette;
          palette2MetSpec = metallicSpecular;
          palette2Spec = specular;
        }
      } else {
        palette1 = palette;
        palette1MetSpec = metallicSpecular;
        palette1Spec = specular;
        palette2 = palette;
        palette2MetSpec = metallicSpecular;
        palette2Spec = specular;
      }
    }

    public void SetFacepaintMap(Device device, String facepaintPath) {
      facepaintDDS = "/resources" + facepaintPath;
      FileToShaderResource(ref device, facepaintDDS, ref facepaintSRV, textureFirstMipLevel);
    }
  }
}
