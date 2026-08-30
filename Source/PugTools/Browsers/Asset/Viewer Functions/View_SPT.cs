using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Structured reader for the legacy SpeedTree *.spt stream used by SWTOR.
  /// This parses the original token stream (including RED/beta files), texture
  /// references, embedded leaf meshes, optional BNRY/LTLE collision geometry,
  /// and the EGCD v4/v5 footer. It intentionally does not attempt to reimplement
  /// the proprietary SpeedTree procedural geometry generator.
  /// </summary>
  internal static class ViewSPT {
    private sealed class TokenInfo {
      internal readonly String Name;
      internal readonly String Type;
      internal TokenInfo(String name, String type) { Name = name; Type = type; }
    }

    internal sealed class BezierControlPoint {
      internal Single X;
      internal Single Y;
      internal Single TangentX;
      internal Single TangentY;
      internal Single TangentLength;
    }

    internal sealed class BezierInfo {
      internal String Raw = String.Empty;
      internal Single Min;
      internal Single Max;
      internal Single Variance;
      internal readonly List<BezierControlPoint> ControlPoints = new List<BezierControlPoint>();
    }

    internal sealed class SptRecord {
      internal UInt32 Token;
      internal String Name = String.Empty;
      internal String Type = String.Empty;
      internal Object Value;
      internal Int64 Offset;
    }

    internal sealed class SptInfo {
      internal String FormatId = String.Empty;
      internal readonly List<SptRecord> Records = new List<SptRecord>();
      internal readonly List<String> TextureReferences = new List<String>();
      internal ViewCollision.BnryInfo Collision;
      internal UInt32 CollisionPerMeshCount;
      internal String CollisionMeshName = String.Empty;
      internal ViewCollision.BoundingBox BoundingBox1;
      internal ViewCollision.BoundingBox BoundingBox2;
      internal UInt32 EgcdVersion;
      internal UInt32 EgcdOffset;
      internal Int64 SpeedTreeDataLength;
      internal Int64 FileLength;
    }

    private static readonly Dictionary<UInt32, TokenInfo> Tokens = new Dictionary<UInt32, TokenInfo> {
      { 1000u, new TokenInfo("BeginFile", "string") },
      { 1001u, new TokenInfo("EndFile", "") },
      { 1002u, new TokenInfo("BeginTreeInfo", "") },
      { 1003u, new TokenInfo("EndTreeInfo", "") },
      { 1004u, new TokenInfo("BeginGeneralLeafInfo", "") },
      { 1005u, new TokenInfo("EndGeneralLeafInfo", "") },
      { 1006u, new TokenInfo("LeafLevelCount", "int") },
      { 1007u, new TokenInfo("BeginLeafLevel", "") },
      { 1008u, new TokenInfo("EndLeafLevel", "") },
      { 1009u, new TokenInfo("BeginSingleLeafInfo", "") },
      { 1010u, new TokenInfo("EndSingleLeafInfo", "") },
      { 1011u, new TokenInfo("BeginWindInfo", "") },
      { 1012u, new TokenInfo("EndWindInfo", "") },
      { 1014u, new TokenInfo("BeginBranchInfo", "int") },
      { 1015u, new TokenInfo("EndBranchInfo", "") },
      { 1016u, new TokenInfo("BeginBranchLevel", "") },
      { 1017u, new TokenInfo("EndBranchLevel", "") },
      { 2000u, new TokenInfo("Tree_BranchTexture", "string") },
      { 2001u, new TokenInfo("Tree_Far", "float") },
      { 2002u, new TokenInfo("Tree_LOD", "bool") },
      { 2003u, new TokenInfo("Tree_Near", "float") },
      { 2005u, new TokenInfo("Tree_RandomSeed", "uint") },
      { 2006u, new TokenInfo("Tree_Size", "float") },
      { 2007u, new TokenInfo("Tree_Size_Variance", "float") },
      { 3000u, new TokenInfo("Leaf_Blossom_Distance", "float") },
      { 3001u, new TokenInfo("Leaf_Blossom_Level", "int") },
      { 3002u, new TokenInfo("Leaf_Blossom_Weighting", "float") },
      { 3003u, new TokenInfo("Leaf_ColorShift", "bool") },
      { 3004u, new TokenInfo("Leaf_ColorShiftScalar", "float") },
      { 3005u, new TokenInfo("Leaf_LightScalar", "float") },
      { 3006u, new TokenInfo("Leaf_Lit", "bool") },
      { 3007u, new TokenInfo("Leaf_SpacingTolerance", "float") },
      { 3008u, new TokenInfo("Leaf_CollisionDetection", "int") },
      { 3009u, new TokenInfo("Leaf_Dimming", "bool") },
      { 3010u, new TokenInfo("Leaf_DimmingScalar", "float") },
      { 4000u, new TokenInfo("Leaf_Blossom", "bool") },
      { 4001u, new TokenInfo("Leaf_Color", "vector") },
      { 4002u, new TokenInfo("Leaf_ColorVariance", "float") },
      { 4003u, new TokenInfo("Leaf_Filename", "string") },
      { 4004u, new TokenInfo("Leaf_Origin", "vector") },
      { 4005u, new TokenInfo("Leaf_Size", "vector") },
      { 4006u, new TokenInfo("Leaf_SizeUsed", "vector") },
      { 4007u, new TokenInfo("Leaf_Ratio", "float") },
      { 5000u, new TokenInfo("Wind_Direction", "vector") },
      { 5001u, new TokenInfo("Wind_BranchOscillation", "vector") },
      { 5002u, new TokenInfo("Wind_LeafOscillation", "vector") },
      { 5003u, new TokenInfo("Wind_BranchFactors", "vector") },
      { 5004u, new TokenInfo("Wind_LeafFactors", "vector") },
      { 5005u, new TokenInfo("Wind_Strength", "float") },
      { 5006u, new TokenInfo("Wind_Enabled", "bool") },
      { 6000u, new TokenInfo("Branch_Disturbance", "string") },
      { 6001u, new TokenInfo("Branch_Gravity", "string") },
      { 6002u, new TokenInfo("Branch_Flexibility", "string") },
      { 6003u, new TokenInfo("Branch_FlexibilityScale", "string") },
      { 6004u, new TokenInfo("Branch_Length", "string") },
      { 6005u, new TokenInfo("Branch_Radius", "string") },
      { 6006u, new TokenInfo("Branch_RadiusScale", "string") },
      { 6007u, new TokenInfo("Branch_StartAngle", "string") },
      { 6008u, new TokenInfo("Branch_CrossSectionSegments", "int") },
      { 6009u, new TokenInfo("Branch_Segments", "int") },
      { 6010u, new TokenInfo("Branch_FirstBranch", "float") },
      { 6011u, new TokenInfo("Branch_LastBranch", "float") },
      { 6012u, new TokenInfo("Branch_Frequency", "float") },
      { 6013u, new TokenInfo("Branch_STile", "float") },
      { 6014u, new TokenInfo("Branch_TTile", "float") },
      { 6015u, new TokenInfo("Branch_STileAbsolute", "bool") },
      { 6016u, new TokenInfo("Branch_TTileAbsolute", "bool") },
      { 6017u, new TokenInfo("Branch_AngleProfile", "string") },
      { 7000u, new TokenInfo("BeginLeafCluster", "") },
      { 7001u, new TokenInfo("EndLeafCluster", "") },
      { 8000u, new TokenInfo("BeginLightingInfo", "") },
      { 8001u, new TokenInfo("EndLightingInfo", "") },
      { 8002u, new TokenInfo("BranchLightingMethod", "float") },
      { 8003u, new TokenInfo("BranchMaterial", "float[13]") },
      { 8004u, new TokenInfo("LeafLightingMethod", "int") },
      { 8005u, new TokenInfo("LeafMaterial", "float[13]") },
      { 8006u, new TokenInfo("LeafLightingAdjustment", "float") },
      { 8007u, new TokenInfo("StaticLightingStyle", "int") },
      { 8008u, new TokenInfo("FrondLightingMethod", "int") },
      { 8009u, new TokenInfo("FrondMaterial", "float[13]") },
      { 9000u, new TokenInfo("BeginLodInfo", "") },
      { 9001u, new TokenInfo("EndLodInfo", "") },
      { 9002u, new TokenInfo("LeafTransitionMethod", "int") },
      { 9003u, new TokenInfo("LeafTransitionRadius", "float") },
      { 9004u, new TokenInfo("LeafCurveExponent", "float") },
      { 9005u, new TokenInfo("BeginEngineLodInfo", "") },
      { 9006u, new TokenInfo("EndEngineLodInfo", "") },
      { 9007u, new TokenInfo("BranchNumLods", "int") },
      { 9008u, new TokenInfo("BranchMinVolumePercent", "float") },
      { 9009u, new TokenInfo("LeafSizeIncreaseFactor", "float") },
      { 9010u, new TokenInfo("LeafReductionFactor", "float") },
      { 9011u, new TokenInfo("LeafNumLods", "int") },
      { 9012u, new TokenInfo("BranchMaxVolumePercent", "float") },
      { 9013u, new TokenInfo("BranchReductionFuzziness", "float") },
      { 9014u, new TokenInfo("LargeBranchPercent", "float") },
      { 10000u, new TokenInfo("BeginTextureCoordInfo", "") },
      { 10001u, new TokenInfo("EndTextureCoordInfo", "") },
      { 10002u, new TokenInfo("LeafTextureCoords", "float[8][]") },
      { 10003u, new TokenInfo("BillboardTextureCoords", "float[8][]") },
      { 10004u, new TokenInfo("FrondTextureCoords", "float[8][]") },
      { 11000u, new TokenInfo("BeginNewWindInfo", "") },
      { 11001u, new TokenInfo("EndNewWindInfo", "") },
      { 11002u, new TokenInfo("WindLevel", "int") },
      { 12000u, new TokenInfo("BeginCollisionInfo", "") },
      { 12001u, new TokenInfo("EndCollisionInfo", "") },
      { 12002u, new TokenInfo("CollisionSphere", "float[4]") },
      { 12003u, new TokenInfo("CollisionCapsule", "float[5]") },
      { 12004u, new TokenInfo("CollisionBox", "float[6]") },
      { 13000u, new TokenInfo("BeginFrondInfo", "") },
      { 13001u, new TokenInfo("EndFrondInfo", "") },
      { 13002u, new TokenInfo("FrondLevel", "int") },
      { 13003u, new TokenInfo("FrondType", "int") },
      { 13004u, new TokenInfo("FrondNumBlades", "int") },
      { 13005u, new TokenInfo("FrondProfile", "string") },
      { 13006u, new TokenInfo("FrondProfileSegments", "int") },
      { 13007u, new TokenInfo("FrondEnabled", "bool") },
      { 13008u, new TokenInfo("FrondNumTextures", "int") },
      { 13009u, new TokenInfo("FrondNumLodLevels", "int") },
      { 13010u, new TokenInfo("FrondMaxSurfaceAreaPercent", "float") },
      { 13011u, new TokenInfo("FrondMinSurfaceAreaPercent", "float") },
      { 13012u, new TokenInfo("FrondReductionFuzziness", "float") },
      { 13013u, new TokenInfo("FrondLargeFrondPercent", "float") },
      { 14000u, new TokenInfo("BeginFrondTexture", "") },
      { 14001u, new TokenInfo("EndFrondTexture", "") },
      { 14002u, new TokenInfo("FrondTextureFilename", "string") },
      { 14003u, new TokenInfo("FrondTextureAspectRatio", "float") },
      { 14004u, new TokenInfo("FrondTextureSizeScale", "float") },
      { 14005u, new TokenInfo("FrondTextureMinAngleOffset", "float") },
      { 14006u, new TokenInfo("FrondTextureMaxAngleOffset", "float") },
      { 14007u, new TokenInfo("FrondMinLengthSegments", "int") },
      { 14008u, new TokenInfo("FrondMinCrossSegments", "int") },
      { 15000u, new TokenInfo("BeginTextureControls", "") },
      { 15001u, new TokenInfo("EndTextureControls", "") },
      { 15002u, new TokenInfo("TextureOffset", "bool") },
      { 15003u, new TokenInfo("TextureTwist", "float") },
      { 16000u, new TokenInfo("BeginFlareInfo", "") },
      { 16001u, new TokenInfo("EndFlareInfo", "") },
      { 16002u, new TokenInfo("FlareSegmentPackingExponent", "float") },
      { 16003u, new TokenInfo("FlareNumFlares", "int") },
      { 16004u, new TokenInfo("FlareBalance", "float") },
      { 16005u, new TokenInfo("FlareRadialInfluence", "float") },
      { 16006u, new TokenInfo("FlareRadialInfluenceVariance", "float") },
      { 16007u, new TokenInfo("FlareRadialExponent", "float") },
      { 16008u, new TokenInfo("FlareRadialDistance", "float") },
      { 16009u, new TokenInfo("FlareRadialDistanceVariance", "float") },
      { 16010u, new TokenInfo("FlareLengthDistance", "float") },
      { 16011u, new TokenInfo("FlareLengthDistanceVariance", "float") },
      { 16012u, new TokenInfo("LengthExponent", "float") },
      { 16013u, new TokenInfo("FlareSeed", "int") },
      { 16014u, new TokenInfo("LeafTransitionFactor", "float") },
      { 18000u, new TokenInfo("BeginShadowProjectionInfo", "") },
      { 18001u, new TokenInfo("EndShadowProjectionInfo", "") },
      { 18002u, new TokenInfo("ShadowRightVector", "vector") },
      { 18003u, new TokenInfo("ShadowUpVector", "vector") },
      { 18004u, new TokenInfo("ShadowOutVector", "vector") },
      { 18005u, new TokenInfo("ShadowMap", "string") },
      { 19000u, new TokenInfo("BeginUserData", "") },
      { 19001u, new TokenInfo("EndUserData", "") },
      { 19002u, new TokenInfo("UserData", "string") },
      { 20000u, new TokenInfo("BeginSupplementalTexCoordInfo", "") },
      { 20001u, new TokenInfo("EndSupplementalTexCoordInfo", "") },
      { 20002u, new TokenInfo("SupplementalCompositeFilename", "string") },
      { 20003u, new TokenInfo("SupplementalHorizontalBillboard", "bool") },
      { 20004u, new TokenInfo("Supplemental360Billboard", "bool") },
      { 20005u, new TokenInfo("SupplementalShadowTexCoords", "float[8]") },
      { 21000u, new TokenInfo("SpeedWindRockScalar", "float") },
      { 21001u, new TokenInfo("SpeedWindRustleScalar", "float") },
      { 22000u, new TokenInfo("PropagateFlexibility", "bool") },
      { 23002u, new TokenInfo("LightSeamStartBias", "float") },
      { 23003u, new TokenInfo("LightSeamEndBias", "float") },
      { 25000u, new TokenInfo("BeginSupplementalFrondInfo", "") },
      { 25001u, new TokenInfo("EndSupplementalFrondInfo", "") },
      { 25002u, new TokenInfo("FrondDistancePercent", "float") },
      { 25003u, new TokenInfo("FrondDistanceLevel", "int") },
      { 25004u, new TokenInfo("FrondAboveCondition", "int") },
      { 25005u, new TokenInfo("FrondBelowCondition", "int") },
      { 25006u, new TokenInfo("FrondSegmentOverride", "int") },
      { 25007u, new TokenInfo("FrondUseSegmentOverride", "bool") },
      { 26000u, new TokenInfo("BeginSupplementalBranchInfo", "") },
      { 26001u, new TokenInfo("EndSupplementalBranchInfo", "") },
      { 26002u, new TokenInfo("BeginBranch", "") },
      { 26003u, new TokenInfo("EndBranch", "") },
      { 26004u, new TokenInfo("BranchRoughness", "float") },
      { 26005u, new TokenInfo("BranchMinLengthPercent", "float") },
      { 26006u, new TokenInfo("BranchMinCrossSectionPercent", "float") },
      { 26007u, new TokenInfo("BranchPruningPercent", "float") },
      { 26008u, new TokenInfo("BranchPruningDepth", "int") },
      { 26009u, new TokenInfo("ForkEnabled", "bool") },
      { 26010u, new TokenInfo("ForkBias", "float") },
      { 26011u, new TokenInfo("ForkAngle", "float") },
      { 26012u, new TokenInfo("ForkLimit", "int") },
      { 26013u, new TokenInfo("CrossSectionProfile", "string") },
      { 26014u, new TokenInfo("LightSeamProfile", "string") },
      { 26015u, new TokenInfo("BranchRoughnessVerticalFrequency", "float") },
      { 26016u, new TokenInfo("BranchRoughnessHorizontalFrequency", "float") },
      { 26017u, new TokenInfo("BranchRandomRoughness", "float") },
      { 26018u, new TokenInfo("RoughnessProfile", "string") },
      { 26019u, new TokenInfo("FrequencyProfile", "string") },
      { 26020u, new TokenInfo("LightSeamBias", "string") },
      { 26021u, new TokenInfo("Gnarl", "float") },
      { 26022u, new TokenInfo("GnarlProfile", "string") },
      { 26023u, new TokenInfo("GnarlUnison", "bool") },
      { 27000u, new TokenInfo("BeginFloorInfo", "") },
      { 27001u, new TokenInfo("EndFloorInfo", "") },
      { 27002u, new TokenInfo("FloorEnabled", "bool") },
      { 27003u, new TokenInfo("FloorValue", "float") },
      { 27004u, new TokenInfo("FloorLevel", "int") },
      { 27005u, new TokenInfo("FloorExponent", "float") },
      { 27006u, new TokenInfo("FloorBias", "float") },
      { 28000u, new TokenInfo("BeginLeafNormalSmoothing", "") },
      { 28001u, new TokenInfo("EndLeafNormalSmoothing", "") },
      { 28002u, new TokenInfo("LeafSmoothingEnabled", "bool") },
      { 28003u, new TokenInfo("LeafSmoothingAngle", "float") },
      { 28004u, new TokenInfo("LeafDimmingDepth", "int") },
      { 29000u, new TokenInfo("BeginClusterInfo", "") },
      { 29001u, new TokenInfo("EndClusterInfo", "") },
      { 29002u, new TokenInfo("FirstBranchLevel", "int") },
      { 30000u, new TokenInfo("BeginStandardShaderInfo", "") },
      { 30001u, new TokenInfo("EndStandardShaderInfo", "") },
      { 30002u, new TokenInfo("BranchLightScalar", "float") },
      { 30003u, new TokenInfo("FrondLightScalar", "float") },
      { 30004u, new TokenInfo("LeafLightScalar", "float") },
      { 30005u, new TokenInfo("GlobalLightScalar", "float") },
      { 30006u, new TokenInfo("AmbientScalar", "float") },
      { 30007u, new TokenInfo("BillboardDarkSideLightScalar", "float") },
      { 30008u, new TokenInfo("BillboardAmbientScalar", "float") },
      { 30009u, new TokenInfo("BillboardBrightSideLightScalar", "float") },
      { 40000u, new TokenInfo("BeginRootSupportInfo", "") },
      { 40001u, new TokenInfo("EndRootSupportInfo", "") },
      { 40002u, new TokenInfo("RootLevel", "int") },
      { 40003u, new TokenInfo("RootFirst", "float") },
      { 40004u, new TokenInfo("RootLast", "float") },
      { 40005u, new TokenInfo("RootPercentage", "float") },
      { 40006u, new TokenInfo("RootData", "") },
      { 40007u, new TokenInfo("BeginSupplementalBranchData", "") },
      { 40008u, new TokenInfo("EndSupplementalBranchData", "") },
      { 50000u, new TokenInfo("BeginTexCoordControls", "") },
      { 50001u, new TokenInfo("EndTexCoordControls", "") },
      { 50002u, new TokenInfo("BeginControl", "") },
      { 50003u, new TokenInfo("EndControl", "") },
      { 50004u, new TokenInfo("Tex_STile", "float") },
      { 50005u, new TokenInfo("Tex_TTile", "float") },
      { 50006u, new TokenInfo("Tex_STileAbsolute", "bool") },
      { 50007u, new TokenInfo("Tex_TTileAbsolute", "bool") },
      { 50008u, new TokenInfo("Tex_Twist", "float") },
      { 50009u, new TokenInfo("Tex_RandomTOffset", "bool") },
      { 50010u, new TokenInfo("Tex_TOffset", "float") },
      { 50011u, new TokenInfo("Tex_ClampS", "bool") },
      { 50012u, new TokenInfo("Tex_ClampT", "bool") },
      { 50013u, new TokenInfo("Tex_ClampLeft", "float") },
      { 50014u, new TokenInfo("Tex_ClampRight", "float") },
      { 50015u, new TokenInfo("Tex_ClampBottom", "float") },
      { 50016u, new TokenInfo("Tex_ClampTop", "float") },
      { 50017u, new TokenInfo("Tex_SOffset", "float") },
      { 50018u, new TokenInfo("Tex_Synch", "bool") },
      { 60000u, new TokenInfo("BeginMapBank", "") },
      { 60001u, new TokenInfo("EndMapBank", "") },
      { 60002u, new TokenInfo("MapBank_Branches", "") },
      { 60003u, new TokenInfo("MapBank_Leaves", "") },
      { 60004u, new TokenInfo("MapBank_Fronds", "") },
      { 60005u, new TokenInfo("MapBank_Composite", "") },
      { 60006u, new TokenInfo("MapBank_SelfShadow", "string") },
      { 60007u, new TokenInfo("MapBank_NumLeafMaps", "int") },
      { 60008u, new TokenInfo("MapBank_NumFrondMaps", "int") },
      { 60009u, new TokenInfo("MapBank_Billboard", "") },
      { 70000u, new TokenInfo("BeginMapCollection", "") },
      { 70001u, new TokenInfo("EndMapCollection", "") },
      { 70002u, new TokenInfo("MapCollection_Diffuse", "string") },
      { 70003u, new TokenInfo("MapCollection_Detail", "string") },
      { 70004u, new TokenInfo("MapCollection_Normal", "string") },
      { 70005u, new TokenInfo("MapCollection_Height", "string") },
      { 70006u, new TokenInfo("MapCollection_Specular", "string") },
      { 70007u, new TokenInfo("MapCollection_User1", "string") },
      { 70008u, new TokenInfo("MapCollection_User2", "string") },
      { 71000u, new TokenInfo("BeginMeshes", "") },
      { 71001u, new TokenInfo("NumMeshes", "int") },
      { 71002u, new TokenInfo("BeginMesh", "") },
      { 71003u, new TokenInfo("Mesh_Name", "string") },
      { 71004u, new TokenInfo("Mesh_VertexCount", "int") },
      { 71005u, new TokenInfo("Mesh_BeginVertex", "") },
      { 71006u, new TokenInfo("Mesh_VertexPos", "vector") },
      { 71007u, new TokenInfo("Mesh_VertexNormal", "vector") },
      { 71008u, new TokenInfo("Mesh_VertexBinormal", "vector") },
      { 71009u, new TokenInfo("Mesh_VertexTangent", "vector") },
      { 71010u, new TokenInfo("Mesh_VertexTexCoord", "float[2]") },
      { 71011u, new TokenInfo("Mesh_EndVertex", "") },
      { 71012u, new TokenInfo("Mesh_IndexCount", "int") },
      { 71013u, new TokenInfo("Mesh_Index", "int") },
      { 71014u, new TokenInfo("EndMesh", "") },
      { 71015u, new TokenInfo("EndMeshes", "") },
      { 72000u, new TokenInfo("BeginLeafMeshInfo", "") },
      { 72001u, new TokenInfo("LeafMesh_UseMeshes", "bool") },
      { 72003u, new TokenInfo("LeafMesh_Index", "float") },
      { 72004u, new TokenInfo("EndLeafMeshInfo", "") },
      { 72005u, new TokenInfo("LeafMesh_Hang", "float") },
      { 72006u, new TokenInfo("LeafMesh_Rotate", "float") },
      { 73000u, new TokenInfo("BeginSupplementalCollisionObjectInfo", "") },
      { 73001u, new TokenInfo("EndSupplementalCollisionObjectInfo", "") },
      { 73002u, new TokenInfo("BeginCollisionObject", "") },
      { 73003u, new TokenInfo("EndCollisionObject", "") },
      { 73004u, new TokenInfo("CollisionRotateX", "float") },
      { 73005u, new TokenInfo("CollisionRotateY", "float") },
      { 73006u, new TokenInfo("CollisionRotateZ", "float") },
      { 74000u, new TokenInfo("BeginSupplementalGlobalInfo", "") },
      { 74001u, new TokenInfo("EndSupplementalGlobalInfo", "") },
      { 74002u, new TokenInfo("TreeAngle", "float") },
      { 75000u, new TokenInfo("BeginSupplementalLodInfo", "") },
      { 75001u, new TokenInfo("EndSupplementalLodInfo", "") },
      { 75002u, new TokenInfo("BranchLodFadeDistance", "float") },
      { 75003u, new TokenInfo("FrondLodFadeDistance", "float") },
      { 75004u, new TokenInfo("ApplyFadingToExtrusions", "bool") },
      { 75005u, new TokenInfo("BillboardTransitionFactor", "float") },
    };

    internal static SptInfo Parse(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      if (!input.CanSeek) throw new InvalidDataException("SPT stream must be seekable.");
      input.Position = 0;
      using BinaryReader br = new BinaryReader(input, Encoding.UTF8, true);
      SptInfo info = new SptInfo { FileLength = input.Length };
      HashSet<String> textures = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

      while (input.Position < input.Length) {
        Int64 offset = input.Position;
        if (input.Length - input.Position < 4) throw new InvalidDataException("Truncated SPT token at 0x" + offset.ToString("X") + ".");
        UInt32 token = br.ReadUInt32();
        if (!Tokens.TryGetValue(token, out TokenInfo tokenInfo)) {
          input.Position = offset;
          break;
        }

        SptRecord record = new SptRecord {
          Token = token,
          Name = tokenInfo.Name,
          Type = tokenInfo.Type,
          Offset = offset
        };
        if (!String.IsNullOrEmpty(tokenInfo.Type)) record.Value = ReadValue(br, tokenInfo.Type);
        info.Records.Add(record);

        if (token == 1000 && record.Value is String id) info.FormatId = id;
        CollectTexture(record.Value, textures);

        if (token == 75001) break;
      }

      info.SpeedTreeDataLength = input.Position;
      info.TextureReferences.AddRange(textures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

      if (input.Position < input.Length) {
        Int64 collisionStart = input.Position;
        info.Collision = ViewCollision.ParseBnry(br, collisionStart);
        if (info.Collision != null) {
          EnsureAvailable(input, 8);
          info.CollisionPerMeshCount = br.ReadUInt32();
          UInt32 one = br.ReadUInt32();
          if (one != 1) throw new InvalidDataException("Expected 1 before SPT collision mesh name, got " + one + ".");
        }

        info.CollisionMeshName = ViewCollision.ReadString32(br);
        if (info.Collision != null && !String.Equals(info.CollisionMeshName, "speedtree_collision", StringComparison.Ordinal))
          throw new InvalidDataException("Expected SPT collision mesh name \"speedtree_collision\", got \"" + info.CollisionMeshName + "\".");
        if (info.Collision == null && !String.IsNullOrEmpty(info.CollisionMeshName))
          throw new InvalidDataException("SPT has a collision mesh name but no BNRY geometry.");

        info.BoundingBox1 = ViewCollision.ReadPlainBoundingBox(br);
        info.BoundingBox2 = ViewCollision.ReadPlainBoundingBox(br);
        ViewCollision.ReadEgcdFooter(br, collisionStart, out UInt32 version, out UInt32 egcdOffset);
        info.EgcdVersion = version;
        info.EgcdOffset = egcdOffset;

        if (input.Position != input.Length)
          throw new InvalidDataException("SPT parser stopped with " + (input.Length - input.Position) + " trailing bytes.");
      }

      if (info.Records.Count == 0)
        throw new InvalidDataException("No SpeedTree tokens were found.");
      if (info.Records[0].Token != 1000)
        throw new InvalidDataException("SPT does not begin with BeginFile token 1000.");
      return info;
    }

    internal static ArrayList BuildTree(SptInfo info, String sourcePath) {
      ArrayList roots = new ArrayList();
      if (info == null) return roots;

      NodeListItem header = new NodeListItem("SpeedTree", String.IsNullOrWhiteSpace(info.FormatId) ? "legacy SPT" : info.FormatId);
      header.children.Add(new NodeListItem("File size", info.FileLength.ToString("N0", CultureInfo.InvariantCulture) + " bytes"));
      header.children.Add(new NodeListItem("Token records", info.Records.Count));
      header.children.Add(new NodeListItem("SpeedTree data length", info.SpeedTreeDataLength.ToString("N0", CultureInfo.InvariantCulture) + " bytes"));
      header.children.Add(new NodeListItem("Texture references", info.TextureReferences.Count));
      header.children.Add(new NodeListItem("Collision", info.Collision == null ? "none" : (info.Collision.Is64Bit ? "BNRY marker 5 (64-bit)" : "BNRY marker 4 (beta/32-bit)")));
      if (info.EgcdVersion != 0) header.children.Add(new NodeListItem("EGCD version", info.EgcdVersion));
      roots.Add(header);

      if (info.TextureReferences.Count > 0) {
        NodeListItem textures = new NodeListItem("Textures", info.TextureReferences.Count + " references");
        foreach (String raw in info.TextureReferences) {
          String resolved = ResolveTexturePath(raw, sourcePath);
          NodeListItem row = new NodeListItem(raw, String.IsNullOrWhiteSpace(resolved) ? String.Empty : resolved);
          if (!String.IsNullOrWhiteSpace(resolved) && !String.Equals(raw, resolved, StringComparison.OrdinalIgnoreCase))
            row.children.Add(new NodeListItem("Archive path candidate", resolved));
          textures.children.Add(row);
        }
        roots.Add(textures);
      }

      NodeListItem streamRoot = new NodeListItem("SPT token stream", info.Records.Count + " records");
      Stack<NodeListItem> stack = new Stack<NodeListItem>();
      stack.Push(streamRoot);
      foreach (SptRecord record in info.Records) {
        if (record.Name.StartsWith("End", StringComparison.Ordinal) && stack.Count > 1) {
          stack.Pop();
          continue;
        }

        NodeListItem row = BuildRecordNode(record);
        stack.Peek().children.Add(row);
        if (record.Name.StartsWith("Begin", StringComparison.Ordinal)) stack.Push(row);
      }
      roots.Add(streamRoot);

      if (info.BoundingBox1 != null || info.BoundingBox2 != null) {
        NodeListItem footer = new NodeListItem("SPT collision/footer", info.Collision == null ? "no collision mesh" : info.CollisionMeshName);
        footer.children.Add(new NodeListItem("Bounding box 1", ViewCollision.FormatBox(info.BoundingBox1)));
        footer.children.Add(new NodeListItem("Bounding box 2", ViewCollision.FormatBox(info.BoundingBox2)));
        footer.children.Add(new NodeListItem("EGCD version", info.EgcdVersion));
        footer.children.Add(new NodeListItem("EGCD offset", "0x" + info.EgcdOffset.ToString("X")));
        if (info.Collision != null) {
          footer.children.Add(new NodeListItem("Per-mesh count", info.CollisionPerMeshCount));
          footer.children.Add(ViewCollision.BuildBnryNode(info.Collision));
        }
        roots.Add(footer);
      }

      return roots;
    }

    internal static IEnumerable<String> ResolvedTexturePaths(SptInfo info, String sourcePath) {
      if (info == null) yield break;
      HashSet<String> emitted = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      foreach (String raw in info.TextureReferences) {
        String resolved = ResolveTexturePath(raw, sourcePath);
        if (!String.IsNullOrWhiteSpace(resolved) && emitted.Add(resolved)) yield return resolved;
      }
    }

    internal static String ResolveTexturePath(String value, String sourcePath) {
      if (String.IsNullOrWhiteSpace(value)) return null;
      String p = value.Trim().Replace('\\', '/');
      if (!LooksLikeTexture(p)) return null;
      if (p.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        p = p.Substring(0, p.Length - 4) + ".dds";

      if (p.StartsWith("z:/mmo1/art/", StringComparison.OrdinalIgnoreCase))
        return NormalizeArchivePath("/resources/art/" + p.Substring("z:/mmo1/art/".Length));
      if (p.StartsWith("y:/art/", StringComparison.OrdinalIgnoreCase))
        return NormalizeArchivePath("/resources/art/" + p.Substring("y:/art/".Length));
      if (p.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
        return NormalizeArchivePath(p);

      if (p.IndexOf('/') < 0 && !String.IsNullOrWhiteSpace(sourcePath)) {
        String normalizedSource = sourcePath.Replace('\\', '/');
        Int32 slash = normalizedSource.LastIndexOf('/');
        if (slash >= 0) return NormalizeArchivePath(normalizedSource.Substring(0, slash + 1) + p);
      }
      return NormalizeArchivePath(p.StartsWith("/") ? p : "/resources/" + p.TrimStart('/'));
    }

    private static Object ReadValue(BinaryReader br, String type) {
      switch (type) {
        case "bool": {
          Byte v = br.ReadByte();
          if (v > 1) throw new InvalidDataException("SPT boolean has invalid value " + v + ".");
          return v != 0;
        }
        case "float": return br.ReadSingle();
        case "int": {
          Int32 v = br.ReadInt32();
          if (v < -1) throw new InvalidDataException("SPT signed integer is below -1: " + v + ".");
          return v;
        }
        case "uint": return br.ReadUInt32();
        case "string": {
          String value = ViewCollision.ReadString32(br);
          return TryParseBezier(value, out BezierInfo spline) ? spline : value;
        }
        case "vector": return ReadFloatArray(br, 3);
        case "float[2]": return ReadFloatArray(br, 2);
        case "float[4]": return ReadFloatArray(br, 4);
        case "float[5]": return ReadFloatArray(br, 5);
        case "float[6]": return ReadFloatArray(br, 6);
        case "float[8]": return ReadFloatArray(br, 8);
        case "float[13]": return ReadFloatArray(br, 13);
        case "float[8][]": {
          UInt32 count = br.ReadUInt32();
          if (count > 1000000) throw new InvalidDataException("SPT float[8][] count is unreasonable: " + count + ".");
          List<Single[]> rows = new List<Single[]>(checked((Int32)count));
          for (UInt32 i = 0; i < count; i++) rows.Add(ReadFloatArray(br, 8));
          return rows;
        }
        default:
          throw new InvalidDataException("Unsupported SPT token type \"" + type + "\".");
      }
    }

    private static NodeListItem BuildRecordNode(SptRecord record) {
      String value = FormatValue(record.Value);
      NodeListItem row = new NodeListItem(record.Name + " [" + record.Token + "]", value);
      row.children.Add(new NodeListItem("Offset", "0x" + record.Offset.ToString("X")));
      if (!String.IsNullOrWhiteSpace(record.Type)) row.children.Add(new NodeListItem("Type", record.Type));

      if (record.Value is BezierInfo spline) {
        row.children.Add(new NodeListItem("Range", String.Format(CultureInfo.InvariantCulture, "{0:0.#####} .. {1:0.#####}", spline.Min, spline.Max)));
        row.children.Add(new NodeListItem("Variance", spline.Variance.ToString("0.#####", CultureInfo.InvariantCulture)));
        NodeListItem cps = new NodeListItem("Control points", spline.ControlPoints.Count + " entries");
        for (Int32 i = 0; i < spline.ControlPoints.Count; i++) {
          BezierControlPoint p = spline.ControlPoints[i];
          cps.children.Add(new NodeListItem("#" + i,
            String.Format(CultureInfo.InvariantCulture, "p({0:0.#####}, {1:0.#####}) t({2:0.#####}, {3:0.#####}) len {4:0.#####}",
              p.X, p.Y, p.TangentX, p.TangentY, p.TangentLength)));
        }
        row.children.Add(cps);
      } else if (record.Value is List<Single[]> rows) {
        NodeListItem values = new NodeListItem("Rows", rows.Count + " entries");
        for (Int32 i = 0; i < rows.Count; i++) values.children.Add(new NodeListItem("#" + i, FormatFloatArray(rows[i])));
        row.children.Add(values);
      }
      return row;
    }

    private static Boolean TryParseBezier(String value, out BezierInfo spline) {
      spline = null;
      if (String.IsNullOrEmpty(value) || !value.StartsWith("BezierSpline", StringComparison.Ordinal)) return false;
      try {
        String[] lines = value.Replace("\r", String.Empty).Split('\n');
        String[] first = lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 4) return false;
        BezierInfo parsed = new BezierInfo {
          Raw = value,
          Min = Single.Parse(first[1], CultureInfo.InvariantCulture),
          Max = Single.Parse(first[2], CultureInfo.InvariantCulture),
          Variance = Single.Parse(first[3], CultureInfo.InvariantCulture)
        };
        if (lines.Length < 4 || !Int32.TryParse(lines[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 count) || count < 0) return false;
        if (3 + count > lines.Length) return false;
        for (Int32 i = 0; i < count; i++) {
          String[] p = lines[3 + i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
          if (p.Length < 5) return false;
          parsed.ControlPoints.Add(new BezierControlPoint {
            X = Single.Parse(p[0], CultureInfo.InvariantCulture),
            Y = Single.Parse(p[1], CultureInfo.InvariantCulture),
            TangentX = Single.Parse(p[2], CultureInfo.InvariantCulture),
            TangentY = Single.Parse(p[3], CultureInfo.InvariantCulture),
            TangentLength = Single.Parse(p[4], CultureInfo.InvariantCulture)
          });
        }
        spline = parsed;
        return true;
      } catch {
        spline = null;
        return false;
      }
    }

    private static void CollectTexture(Object value, HashSet<String> textures) {
      if (value is String text && LooksLikeTexture(text)) textures.Add(text);
    }

    private static Boolean LooksLikeTexture(String value) {
      if (String.IsNullOrWhiteSpace(value)) return false;
      String v = value.Trim();
      return v.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
          || v.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private static String NormalizeArchivePath(String path) {
      if (String.IsNullOrWhiteSpace(path)) return path;
      String p = path.Replace('\\', '/');
      while (p.Contains("//")) p = p.Replace("//", "/");
      return p.ToLowerInvariant();
    }

    private static Single[] ReadFloatArray(BinaryReader br, Int32 count) {
      Single[] v = new Single[count];
      for (Int32 i = 0; i < count; i++) v[i] = br.ReadSingle();
      return v;
    }

    private static String FormatValue(Object value) {
      if (value == null) return String.Empty;
      if (value is Boolean b) return b ? "true" : "false";
      if (value is Single f) return f.ToString("0.#######", CultureInfo.InvariantCulture);
      if (value is Single[] a) return FormatFloatArray(a);
      if (value is BezierInfo spline)
        return String.Format(CultureInfo.InvariantCulture, "BezierSpline {0:0.#####}..{1:0.#####}, {2} control points", spline.Min, spline.Max, spline.ControlPoints.Count);
      if (value is List<Single[]> rows) return rows.Count + " rows";
      return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static String FormatFloatArray(Single[] values) =>
      "(" + String.Join(", ", values.Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture))) + ")";

    private static void EnsureAvailable(Stream stream, Int64 bytes) {
      if (bytes < 0 || stream.Position > stream.Length - bytes) throw new EndOfStreamException("SPT footer is truncated.");
    }
  }
}
