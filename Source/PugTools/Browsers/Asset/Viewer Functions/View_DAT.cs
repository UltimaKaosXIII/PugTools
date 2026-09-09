using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using TorArchive;

namespace PugTools {
  /// <summary>
  /// Jedipedia-style reader for SWTOR area.dat and room .dat files.
  /// Supports the current binary formats and the legacy text formats.
  /// </summary>
  internal static class View_DAT {
    private sealed class RoomPropertyInfo {
      internal String Name { get; }
      internal String Type { get; }

      internal RoomPropertyInfo(String name, String type) {
        Name = name;
        Type = type;
      }
    }

    private static readonly Dictionary<UInt32, RoomPropertyInfo> RoomProperties =
      new Dictionary<UInt32, RoomPropertyInfo> {
      { 0x40865E7Fu, new RoomPropertyInfo("ParentInstance", "id") },
      { 0x4F77E269u, new RoomPropertyInfo("Position", "vector3") },
      { 0xB181622Au, new RoomPropertyInfo("Scale", "vector3") },
      { 0x8C64AF1Eu, new RoomPropertyInfo("Rotation", "vector3") },
      { 0xE3D8F636u, new RoomPropertyInfo("IgnoreForCameraCollision", "bool") },
      { 0x394C80E8u, new RoomPropertyInfo("IgnoreForNPCCollision", "bool") },
      { 0x1219A6EEu, new RoomPropertyInfo("PortalTag", "bool") },
      { 0x3C472B4Au, new RoomPropertyInfo("Hidden", "bool") },
      { 0x39801EBAu, new RoomPropertyInfo("Tag", "string") },
      { 0xEA44CDFAu, new RoomPropertyInfo("IgnoreForPlayerCollision", "bool") },
      { 0xEE3CA46Eu, new RoomPropertyInfo("movable", "bool") },
      { 0x71A31565u, new RoomPropertyInfo("Viewability", "enum") },
      { 0x42DD5417u, new RoomPropertyInfo("InheritParentVisibility", "bool") },
      { 0x67AE7960u, new RoomPropertyInfo("AutoGroundSnap", "bool") },
      { 0xB4375BA7u, new RoomPropertyInfo("AutoGroundSnapHeight", "float") },
      { 0x699018B6u, new RoomPropertyInfo("Selectable", "bool") },
      { 0x5D0DF45Fu, new RoomPropertyInfo("Comment", "string") },
      { 0xA8120517u, new RoomPropertyInfo("spnMovePointNumber", "int32") },
      { 0xA8F9F521u, new RoomPropertyInfo("IsUtilityObject", "bool") },
      { 0x50D6785Eu, new RoomPropertyInfo("envMaterialIndex", "int32") },
      { 0x71BE8530u, new RoomPropertyInfo("IsLargeObject", "bool") },
      { 0x4FD0EA45u, new RoomPropertyInfo("Pathability", "enum") },
      { 0x4354F179u, new RoomPropertyInfo("unk_magBoolean", "bool") },
      { 0x94040B70u, new RoomPropertyInfo("Branches", "bool") },
      { 0x77F452DAu, new RoomPropertyInfo("Fronds", "bool") },
      { 0xD1F0FB69u, new RoomPropertyInfo("LeafTinting", "vector4") },
      { 0x028FD91Cu, new RoomPropertyInfo("Leaves", "bool") },
      { 0x1C185975u, new RoomPropertyInfo("DropToBillboard", "bool") },
      { 0xB1BCF795u, new RoomPropertyInfo("OverrideLOD", "bool") },
      { 0x702B73D9u, new RoomPropertyInfo("NearLOD", "float") },
      { 0x7FE559AAu, new RoomPropertyInfo("FarLOD", "float") },
      { 0x124BC8ECu, new RoomPropertyInfo("LeafScale", "float") },
      { 0x1AA15A81u, new RoomPropertyInfo("DiffuseColor", "") },
      { 0xCAE0F72Du, new RoomPropertyInfo("MouseTransparent", "bool") },
      { 0x5C0DA950u, new RoomPropertyInfo("MouseTargetable", "bool") },
      { 0x9FDC4087u, new RoomPropertyInfo("OmniShadowCastingStyle", "enum") },
      { 0x20447338u, new RoomPropertyInfo("DirectionalShadowCastingStyle", "enum") },
      { 0xB4963DC1u, new RoomPropertyInfo("Collidable", "bool") },
      { 0xB04139FDu, new RoomPropertyInfo("PortalTarget", "string") },
      { 0x9F0F3F8Cu, new RoomPropertyInfo("AnimatedPosition", "string") },
      { 0x01C98976u, new RoomPropertyInfo("ProjectOnHeightmaps", "bool") },
      { 0x72960AC7u, new RoomPropertyInfo("AnimatedScale", "string") },
      { 0xDBFC0C41u, new RoomPropertyInfo("AnimatedRotation", "string") },
      { 0xB300AF4Cu, new RoomPropertyInfo("ProjectionTexture", "string") },
      { 0x13C6CC74u, new RoomPropertyInfo("ProjectionColor", "string") },
      { 0x1AB356BCu, new RoomPropertyInfo("ProjectOnObjects", "bool") },
      { 0x4970A5C2u, new RoomPropertyInfo("ProjectOnCharacters", "bool") },
      { 0xCAA08C29u, new RoomPropertyInfo("ProjectionType", "enum") },
      { 0xF357296Du, new RoomPropertyInfo("ProjectOnSpeedTree", "bool") },
      { 0xA6424A9Fu, new RoomPropertyInfo("ProjectOnWater", "bool") },
      { 0xAA53F027u, new RoomPropertyInfo("DeltaRotation3D", "string") },
      { 0x8B335065u, new RoomPropertyInfo("Path", "string") },
      { 0x60CAA0A1u, new RoomPropertyInfo("StartingWaypoint", "int32") },
      { 0x7ECB1513u, new RoomPropertyInfo("BSpline", "bool") },
      { 0xBD389DDDu, new RoomPropertyInfo("TurnRate", "float") },
      { 0xC9D0FF28u, new RoomPropertyInfo("SlowdownWhenTurning", "float") },
      { 0x1A0A654Cu, new RoomPropertyInfo("AutoRoll", "float") },
      { 0xD1B0D9ACu, new RoomPropertyInfo("WaypointTolerance", "float") },
      { 0x928EDB76u, new RoomPropertyInfo("Pause", "bool") },
      { 0x12507969u, new RoomPropertyInfo("Spiro", "string") },
      { 0x2E8FEA76u, new RoomPropertyInfo("End_Behavior", "enum") },
      { 0x49380DADu, new RoomPropertyInfo("Time", "float") },
      { 0xB9E18D65u, new RoomPropertyInfo("SpeedCorrection", "bool") },
      { 0x8CDA4F8Du, new RoomPropertyInfo("Tolerance", "float") },
      { 0x9BABB246u, new RoomPropertyInfo("targetinstance", "id") },
      { 0xCAD566AFu, new RoomPropertyInfo("BoneName", "string") },
      { 0x70294113u, new RoomPropertyInfo("Offset", "vector3") },
      { 0x8D913CAEu, new RoomPropertyInfo("AllowCallback", "bool") },
      { 0x3E2862E4u, new RoomPropertyInfo("DistanceFactor", "float") },
      { 0xC483EFBAu, new RoomPropertyInfo("Facing", "bool") },
      { 0x95D554F8u, new RoomPropertyInfo("StrictMovement", "bool") },
      { 0x104B3827u, new RoomPropertyInfo("Speed", "string") },
      { 0xC2676F38u, new RoomPropertyInfo("FxSpecName", "string") },
      { 0xCC788E2Du, new RoomPropertyInfo("FxRespawnDelay", "string") },
      { 0x53A4748Eu, new RoomPropertyInfo("FxRandomDelay", "float") },
      { 0x65D2E050u, new RoomPropertyInfo("FxMinSpawnDistance", "float") },
      { 0x5896DCBEu, new RoomPropertyInfo("FxMaxSpawnDistance", "float") },
      { 0x0A383A8Fu, new RoomPropertyInfo("FxUseTargetSpawnVolume", "bool") },
      { 0xA70B0443u, new RoomPropertyInfo("FxSpawnVolume", "vector3") },
      { 0xDF62751Eu, new RoomPropertyInfo("FxCanSpawnOffscreen", "bool") },
      { 0x08EEFC62u, new RoomPropertyInfo("FxLifetimeControl", "enum") },
      { 0x4C777864u, new RoomPropertyInfo("FxPause", "bool") },
      { 0x1F3DE3A4u, new RoomPropertyInfo("FxMuteSound", "bool") },
      { 0x6F81627Eu, new RoomPropertyInfo("FxTargetID", "id") },
      { 0xFE66A912u, new RoomPropertyInfo("FxHueOverride", "string") },
      { 0x38885F86u, new RoomPropertyInfo("PlayEvent", "string") },
      { 0xB92CC758u, new RoomPropertyInfo("StopEvent", "string") },
      { 0x39D7D597u, new RoomPropertyInfo("PlayMode", "enum") },
      { 0x605ADD3Bu, new RoomPropertyInfo("DestroyOnStop", "bool") },
      { 0xEDAA8C23u, new RoomPropertyInfo("VirtualEmitter", "id") },
      { 0x9EB346D7u, new RoomPropertyInfo("SurfaceMapShininess", "float") },
      { 0xA4DEE397u, new RoomPropertyInfo("DeepColor", "vector4") },
      { 0x67D56C2Cu, new RoomPropertyInfo("wtrTextureIndex", "int32") },
      { 0x79252004u, new RoomPropertyInfo("KneeValue2", "float") },
      { 0x22232EB0u, new RoomPropertyInfo("NormalMap2Dir", "float") },
      { 0x309B8EDAu, new RoomPropertyInfo("NormalMap1Rotation", "float") },
      { 0x34B33599u, new RoomPropertyInfo("SurfaceKneePosition2", "float") },
      { 0x97BD57BCu, new RoomPropertyInfo("NormalMap1", "string") },
      { 0xAF219C64u, new RoomPropertyInfo("SurfaceMapCoordScale", "float") },
      { 0x79252005u, new RoomPropertyInfo("KneeValue3", "float") },
      { 0x34B3359Au, new RoomPropertyInfo("SurfaceKneePosition3", "float") },
      { 0x012A9C47u, new RoomPropertyInfo("KneePosition3", "float") },
      { 0x62720B6Bu, new RoomPropertyInfo("NormalMap1Speed", "float") },
      { 0x789E6CAAu, new RoomPropertyInfo("NormalMap2Speed", "float") },
      { 0xEF241B39u, new RoomPropertyInfo("GlossColor", "vector4") },
      { 0x5430EB0Fu, new RoomPropertyInfo("SurfaceMap", "string") },
      { 0x97BD57BDu, new RoomPropertyInfo("NormalMap2", "string") },
      { 0x03B54CDBu, new RoomPropertyInfo("NormalMap2Rotation", "float") },
      { 0x34B33598u, new RoomPropertyInfo("SurfaceKneePosition1", "float") },
      { 0x012A9C46u, new RoomPropertyInfo("KneePosition2", "float") },
      { 0x06C671D8u, new RoomPropertyInfo("DepthTexture", "string") },
      { 0xA0A2E891u, new RoomPropertyInfo("NormalMap1CoordScale", "float") },
      { 0x9458550Cu, new RoomPropertyInfo("DepthModulator", "float") },
      { 0xD0CFBBE7u, new RoomPropertyInfo("wtrIndexData", "string") },
      { 0x073BB612u, new RoomPropertyInfo("NormalMap2CoordScale", "float") },
      { 0x577AA423u, new RoomPropertyInfo("wtrVertexData", "string") },
      { 0xF39C5DF1u, new RoomPropertyInfo("NormalMap1Dir", "float") },
      { 0x6F62ABAFu, new RoomPropertyInfo("ShallowColor", "vector4") },
      { 0x012A9C45u, new RoomPropertyInfo("KneePosition1", "float") },
      { 0x79252003u, new RoomPropertyInfo("KneeValue1", "float") },
      { 0x12265370u, new RoomPropertyInfo("KneeValueAt1", "float") },
      { 0xC7A1E914u, new RoomPropertyInfo("DistanceOpacityScale", "float") },
      { 0xE19C6092u, new RoomPropertyInfo("AngleOpacityScale", "float") },
      { 0x6EB96870u, new RoomPropertyInfo("SurfaceKneeValue1", "float") },
      { 0x6EB96871u, new RoomPropertyInfo("SurfaceKneeValue2", "float") },
      { 0x6EB96872u, new RoomPropertyInfo("SurfaceKneeValue3", "float") },
      { 0x27C3355Cu, new RoomPropertyInfo("SurfaceKneeValueAt0", "float") },
      { 0x27C3355Du, new RoomPropertyInfo("SurfaceKneeValueAt1", "float") },
      { 0xA1DF4EB5u, new RoomPropertyInfo("NormalMapScale", "float") },
      { 0xC4F0C470u, new RoomPropertyInfo("DepthFilterBlurriness", "float") },
      { 0x059903C4u, new RoomPropertyInfo("ReflectionModulator", "float") },
      { 0x172A4EC2u, new RoomPropertyInfo("SpecularPower", "float") },
      { 0xFDA2965Du, new RoomPropertyInfo("rgnVolumeData", "string") },
      { 0x27EEE87Fu, new RoomPropertyInfo("rgnCharacteristics", "string") },
      { 0x63BC6D60u, new RoomPropertyInfo("rgnPvpLocationType", "enum") },
      { 0xEDC1CA7Du, new RoomPropertyInfo("rgnPriority", "int32") },
      { 0xAFF3B819u, new RoomPropertyInfo("rgnClassType", "enum") },
      { 0x1BD46F12u, new RoomPropertyInfo("rgnRespawnMedCenter", "string") },
      { 0x70593352u, new RoomPropertyInfo("unk_MapPage", "string") },
      { 0xC00B5BE5u, new RoomPropertyInfo("unk_MapCoords", "vector3") },
      { 0x0F948846u, new RoomPropertyInfo("skyboxBloomCutoff", "float") },
      { 0x50557AD4u, new RoomPropertyInfo("fogMinDensity", "float") },
      { 0x16387C42u, new RoomPropertyInfo("fogMaxDensity", "float") },
      { 0xECA7CB9Eu, new RoomPropertyInfo("depthTransitionDist", "float") },
      { 0x04A9CC22u, new RoomPropertyInfo("fogDistanceFromCamera", "float") },
      { 0xDAD56EACu, new RoomPropertyInfo("FogColor1", "vector4") },
      { 0xC9E595D6u, new RoomPropertyInfo("fogDistBetweenMinMax", "float") },
      { 0xF185F8D0u, new RoomPropertyInfo("fogColorEndInterpDist", "float") },
      { 0x44491491u, new RoomPropertyInfo("skyboxFogPercent", "float") },
      { 0x828C0658u, new RoomPropertyInfo("unk_enviroregionBoolean", "bool") },
      { 0x8808FB10u, new RoomPropertyInfo("bloomIntensity", "float") },
      { 0x3E32C747u, new RoomPropertyInfo("emissiveBloomIntensity", "float") },
      { 0x86F5623Au, new RoomPropertyInfo("skyboxBloomIntensity", "float") },
      { 0xEA6150ADu, new RoomPropertyInfo("specularBloomCutoff", "float") },
      { 0x907C9713u, new RoomPropertyInfo("specularBloomIntensity", "float") },
      { 0xB6F3C3DBu, new RoomPropertyInfo("regionEdgeData", "string") },
      { 0xDEAA96F8u, new RoomPropertyInfo("edgeTransitionDist", "float") },
      { 0x3A98EFA8u, new RoomPropertyInfo("unk_enviroregionColor", "") },
      { 0xC8D610D7u, new RoomPropertyInfo("fogColorStartInterpDist", "float") },
      { 0xDAD56EADu, new RoomPropertyInfo("FogColor2", "vector4") },
      { 0x5D8D2375u, new RoomPropertyInfo("regionVolumeHeight", "float") },
      { 0x609BF2BCu, new RoomPropertyInfo("FogColorSky", "vector4") },
      { 0xB5B722C2u, new RoomPropertyInfo("heightTransitionDist", "float") },
      { 0x7F39E1FFu, new RoomPropertyInfo("spnMinGroupSize", "int32") },
      { 0xFB12A225u, new RoomPropertyInfo("spnHydraImportance", "bool") },
      { 0x8363D1F5u, new RoomPropertyInfo("unk_SpawnAngleOrDelay1?", "float") },
      { 0x2DC3ED9Fu, new RoomPropertyInfo("spnAggroRadius", "float") },
      { 0x2A887DE0u, new RoomPropertyInfo("spnTagFromEncounter", "string") },
      { 0x37D70C6Du, new RoomPropertyInfo("spnMaxGroupSize", "int32") },
      { 0xB1CF7C7Au, new RoomPropertyInfo("spnHydraReference", "id") },
      { 0xE62F4F4Fu, new RoomPropertyInfo("spnEncounterCombatMusic", "bool") },
      { 0x34B5E2DFu, new RoomPropertyInfo("spnAttentionRadius", "float") },
      { 0x9F03AEA9u, new RoomPropertyInfo("unk_spnBoolean", "bool") },
      { 0xC519C592u, new RoomPropertyInfo("unk_EppOnSpawn", "string") },
      { 0x8EA2A60Bu, new RoomPropertyInfo("spnNpcIdleAnimationName", "string") },
      { 0xB66C482Au, new RoomPropertyInfo("spnPhaseInstanceName", "string") },
      { 0xBCBFD3E8u, new RoomPropertyInfo("ParentMapTag", "string") },
      { 0x3824CB31u, new RoomPropertyInfo("ShowGhosted", "bool") },
      { 0xCB0D5E9Fu, new RoomPropertyInfo("ShowOffmapArrow", "bool") },
      { 0x293E2A74u, new RoomPropertyInfo("ShowQuestBreadcrumbOnly", "bool") },
      { 0x82FB386Bu, new RoomPropertyInfo("DoNotBreadcrumb", "bool") },
      { 0x4D58E5D1u, new RoomPropertyInfo("mpnIgnoreFoW", "bool") },
      { 0x36A29A30u, new RoomPropertyInfo("LightType", "enum") },
      { 0x6CB280CEu, new RoomPropertyInfo("SourceOffset", "float") },
      { 0x16E929FDu, new RoomPropertyInfo("Range", "") },
      { 0x6C84A1CDu, new RoomPropertyInfo("IlluminationMap", "string") },
      { 0xBC68CFF3u, new RoomPropertyInfo("Intensity", "string") },
      { 0xCF95209Du, new RoomPropertyInfo("InnerAngle", "float") },
      { 0x01CDB4FDu, new RoomPropertyInfo("Reflect", "bool") },
      { 0x8FBC0A98u, new RoomPropertyInfo("OuterAngle", "float") },
      { 0x429C878Au, new RoomPropertyInfo("RampMap", "string") },
      { 0x279A7732u, new RoomPropertyInfo("RestrictToRoom", "bool") },
      { 0xDBCFD77Fu, new RoomPropertyInfo("CastsShadows", "bool") },
      { 0xA67AE663u, new RoomPropertyInfo("Color", "") },
      { 0xEA6E4F13u, new RoomPropertyInfo("ShadowAngle", "float") },
      { 0x26794861u, new RoomPropertyInfo("DoBump", "bool") },
      { 0x3EE0998Eu, new RoomPropertyInfo("DoSpecular", "bool") },
      { 0xC5B29609u, new RoomPropertyInfo("DoHeightmaps", "bool") },
      { 0x925224AEu, new RoomPropertyInfo("DoGranny", "bool") },
      { 0x0D59B255u, new RoomPropertyInfo("DoCharacters", "bool") },
      { 0x3B92D7B4u, new RoomPropertyInfo("Falloff", "string") },
      { 0x373E229Eu, new RoomPropertyInfo("CubeShadows", "bool") },
      { 0x1BE9D670u, new RoomPropertyInfo("ShadowDeadZone", "float") },
      { 0x3902C81Au, new RoomPropertyInfo("DoSpeedTree", "bool") },
      { 0xB37CB64Cu, new RoomPropertyInfo("DoWater", "bool") },
      { 0x00E272D3u, new RoomPropertyInfo("unk_Float_7.2", "float") },
      { 0x9B6D63D9u, new RoomPropertyInfo("cvrType", "enum") },
      { 0x594E7800u, new RoomPropertyInfo("cvrDirection", "enum") },
      { 0x7D9CB993u, new RoomPropertyInfo("cvrDirty", "bool") },
      { 0x53953CD4u, new RoomPropertyInfo("cvrEdgePoint", "vector3") },
      { 0xB3D064C2u, new RoomPropertyInfo("cvrEdgeLength", "float") },
      { 0x13738CC3u, new RoomPropertyInfo("cvrEdgeDirection", "vector3") },
      { 0xBC9A0800u, new RoomPropertyInfo("camFollow", "id") },
      { 0xA3AB26AEu, new RoomPropertyInfo("VertexData", "char[]") },
      { 0x696E219Du, new RoomPropertyInfo("tesselation", "string") },
      { 0xDEF7C5F5u, new RoomPropertyInfo("inverted", "bool") },
      { 0x016E860Au, new RoomPropertyInfo("locked", "bool") },
      { 0x0E55396Cu, new RoomPropertyInfo("resolution", "string") },
      { 0xD9DDF326u, new RoomPropertyInfo("Width", "float") },
      { 0x1B205D23u, new RoomPropertyInfo("Depth", "float") },
      { 0x0FA719B1u, new RoomPropertyInfo("CameraSensitive", "bool") },
      { 0xBD8390A3u, new RoomPropertyInfo("TriggerScript", "string") },
      { 0xCEA60141u, new RoomPropertyInfo("ElapsedTimeSinceLastCheck", "float") },
      { 0x03B34D38u, new RoomPropertyInfo("Enter", "bool") },
      { 0x33226D17u, new RoomPropertyInfo("Leave", "bool") },
      { 0xC6AD042Au, new RoomPropertyInfo("Reside", "bool") },
      { 0x5BFA9DA3u, new RoomPropertyInfo("Ellipsoid", "bool") },
      { 0x6561C1B5u, new RoomPropertyInfo("NPCSensitive", "bool") },
      { 0xD84FB395u, new RoomPropertyInfo("TriggerParam", "string") },
      { 0x1612F175u, new RoomPropertyInfo("PlayerSensitive", "bool") },
      { 0xF47AA626u, new RoomPropertyInfo("Active", "bool") },
      { 0x6BD873A1u, new RoomPropertyInfo("Period", "float") },
      { 0xA3A4265Bu, new RoomPropertyInfo("ExistsOn", "enum") },
      { 0x80AF3C1Au, new RoomPropertyInfo("TriggerClassType", "enum") },
      { 0x16B4F247u, new RoomPropertyInfo("Height", "float") },
      { 0x38EC36C3u, new RoomPropertyInfo("Disappear", "bool") },
      { 0x2E1EAD2Bu, new RoomPropertyInfo("name", "string") },
      { 0x7F86386Eu, new RoomPropertyInfo("MaxTorque", "float") },
      { 0xEDC7DA45u, new RoomPropertyInfo("JoinTo", "id") },
      { 0xA42D838Au, new RoomPropertyInfo("swing2Motion", "enum") },
      { 0x6EC30027u, new RoomPropertyInfo("MaxForce", "float") },
      { 0x093836F6u, new RoomPropertyInfo("linearLimit", "string") },
      { 0xCC1F287Du, new RoomPropertyInfo("JointFlags", "enum") },
      { 0x89FFE4A8u, new RoomPropertyInfo("swing1Limit", "string") },
      { 0x9307D9D4u, new RoomPropertyInfo("twistLimit", "string") },
      { 0xA02C45E7u, new RoomPropertyInfo("swing2Limit", "string") },
      { 0xF28FCA2Bu, new RoomPropertyInfo("DriveMotor", "id") },
      { 0x91BC73EEu, new RoomPropertyInfo("xMotion", "enum") },
      { 0x67E7626Fu, new RoomPropertyInfo("yMotion", "enum") },
      { 0x3E1250F0u, new RoomPropertyInfo("zMotion", "enum") },
      { 0xCE029509u, new RoomPropertyInfo("swing1Motion", "enum") },
      { 0xFC23EADDu, new RoomPropertyInfo("twistMotion", "enum") },
      { 0x664E271Cu, new RoomPropertyInfo("PhysicsInstance", "id") },
      { 0xD576D2CFu, new RoomPropertyInfo("aptPlacementLayoutLayoutId", "int32") },
      { 0x65E6548Du, new RoomPropertyInfo("aptPlacementLayoutRoomIndex", "int32") },
      { 0xE5AC13A6u, new RoomPropertyInfo("aptPlacementLayoutDefPermId", "int32") },
      };

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static ArrayList Parse(BinaryReader reader, Assets assets, String directory,
                                    String fileName) {
      if (reader == null) throw new ArgumentNullException(nameof(reader));
      if (!reader.BaseStream.CanSeek)
        throw new InvalidDataException("DAT preview requires a seekable stream.");

      reader.BaseStream.Position = 0;
      String magic = PeekBinaryMagic(reader);
      reader.BaseStream.Position = 0;

      if (String.Equals(magic, "AREA_DAT_BINARY_FORMAT_", StringComparison.Ordinal))
        return ParseAreaBinary(reader);

      if (String.Equals(magic, "ROOM_DAT_BINARY_FORMAT_", StringComparison.Ordinal)) {
        Dictionary<UInt64, String> areaAssets = TryLoadAreaAssetMap(assets, directory);
        reader.BaseStream.Position = 0;
        return ParseRoomBinary(reader, areaAssets);
      }

      reader.BaseStream.Position = 0;
      using MemoryStream copy = new MemoryStream();
      reader.BaseStream.CopyTo(copy);
      String text = DecodeText(copy.ToArray());

      if (text.IndexOf("! Area Specification", StringComparison.OrdinalIgnoreCase) >= 0)
        return ParseAreaText(text);

      if (text.IndexOf("! Room Specification", StringComparison.OrdinalIgnoreCase) >= 0) {
        Dictionary<UInt64, String> areaAssets = TryLoadAreaAssetMap(assets, directory);
        return ParseRoomText(text, areaAssets);
      }

      if (text.IndexOf("! Character Specification", StringComparison.OrdinalIgnoreCase) >= 0)
        return ParseCharacterText(text);

      throw new InvalidDataException(
        "This .dat file is not an SWTOR area, room or character specification file."
      );
    }

    private static String DecodeText(Byte[] data) {
      if (data == null || data.Length == 0) return String.Empty;

      Encoding encoding = Encoding.UTF8;
      Int32 offset = 0;
      Int32 length = data.Length;

      if (length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) {
        encoding = new UTF8Encoding(false, false);
        offset = 3;
      } else if (length >= 2 && data[0] == 0xFF && data[1] == 0xFE) {
        encoding = Encoding.Unicode;
        offset = 2;
      } else if (length >= 2 && data[0] == 0xFE && data[1] == 0xFF) {
        encoding = Encoding.BigEndianUnicode;
        offset = 2;
      } else {
        Int32 sample = Math.Min(length, 256);
        Int32 evenNulls = 0;
        Int32 oddNulls = 0;
        for (Int32 i = 0; i < sample; i++) {
          if (data[i] != 0) continue;
          if ((i & 1) == 0) evenNulls++;
          else oddNulls++;
        }

        if (oddNulls >= 3 && oddNulls > evenNulls * 2)
          encoding = Encoding.Unicode;
        else if (evenNulls >= 3 && evenNulls > oddNulls * 2)
          encoding = Encoding.BigEndianUnicode;
      }

      length -= offset;
      if ((encoding == Encoding.Unicode || encoding == Encoding.BigEndianUnicode) && (length & 1) != 0)
        length--;
      if (length <= 0) return String.Empty;

      return encoding.GetString(data, offset, length).TrimStart('\uFEFF').TrimEnd('\0');
    }

    private static ArrayList ParseCharacterText(String text) {
      String[] lines = Regex.Split(text ?? String.Empty, "\\r\\n|\\n|\\r");
      String character = String.Empty;
      Int32 partsStart = -1;

      for (Int32 i = 0; i < lines.Length; i++) {
        String line = (lines[i] ?? String.Empty).Trim();
        const String characterPrefix = "! Character Specification for ";
        if (line.StartsWith(characterPrefix, StringComparison.OrdinalIgnoreCase))
          character = line.Substring(characterPrefix.Length).Trim();
        if (String.Equals(line, "[PARTS]", StringComparison.OrdinalIgnoreCase)) {
          partsStart = i + 1;
          break;
        }
      }

      if (partsStart < 0)
        throw new InvalidDataException("Character DAT has no [PARTS] section.");

      var parameters = new List<KeyValuePair<String, String>>();
      for (Int32 i = partsStart; i < lines.Length; i++) {
        String line = (lines[i] ?? String.Empty).Trim();
        if (line.Length == 0 || line[0] == '!' || line[0] == ';' || line[0] == '#') continue;
        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) break;
        Int32 equals = line.IndexOf('=');
        String key = equals < 0 ? line : line.Substring(0, equals).Trim();
        String value = equals < 0 ? String.Empty : line.Substring(equals + 1).Trim();
        parameters.Add(new KeyValuePair<String, String>(key, value));
      }

      var roots = new ArrayList();
      NodeListItem summary = Branch("Character Specification");
      summary.children.Add(Leaf("Format", "Text / Version 2"));
      if (!String.IsNullOrWhiteSpace(character)) summary.children.Add(Leaf("Character", character));
      summary.children.Add(Leaf("Parameters", parameters.Count.ToString("N0", Invariant)));
      roots.Add(summary);

      var references = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      if (!String.IsNullOrWhiteSpace(character))
        references.Add(NormalizeResourcePath("/resources/art/dynamic/spec/" + character + ".gr2"));

      NodeListItem parametersRoot = Branch("Parameters (" + parameters.Count.ToString("N0", Invariant) + ")");
      foreach (KeyValuePair<String, String> pair in parameters) {
        NodeListItem parameter = Leaf(pair.Key, pair.Value);
        foreach (String resource in CharacterParameterResources(pair.Key, pair.Value)) {
          references.Add(resource);
          parameter.children.Add(Leaf("Resource", resource));
        }
        parametersRoot.children.Add(parameter);
      }
      roots.Add(parametersRoot);

      if (references.Count > 0) {
        NodeListItem refs = Branch("Referenced Resources (" + references.Count.ToString("N0", Invariant) + ")");
        foreach (String resource in references.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
          refs.children.Add(Leaf(resource, String.Empty));
        roots.Add(refs);
      }
      return roots;
    }

    private static IEnumerable<String> CharacterParameterResources(String key, String value) {
      var result = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      if (String.IsNullOrWhiteSpace(key) || String.IsNullOrWhiteSpace(value)) return result;

      void Add(String path) {
        String normalized = NormalizeResourcePath(path);
        if (!String.IsNullOrWhiteSpace(normalized)) result.Add(normalized);
      }

      switch (key.Trim().ToLowerInvariant()) {
        case "model":
          Add("/resources/art/dynamic/spec/" + value);
          break;
        case "mesh":
        case "animlibraryfqn":
        case "animsharemetadatafqn":
          Add("/resources/" + value);
          break;
        case "material":
          foreach (String material in value.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
            Add("/resources/art/shaders/materials/" + material + ".mat");
          break;
        case "animmetadatafqn":
          foreach (String metadata in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            Add("/resources/" + metadata.Trim());
          break;
      }
      return result;
    }

    private static String NormalizeResourcePath(String path) {
      if (String.IsNullOrWhiteSpace(path)) return String.Empty;
      String normalized = path.Trim().Replace('\\', '/');
      while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
      if (!normalized.StartsWith("/", StringComparison.Ordinal)) normalized = "/" + normalized;
      return normalized.ToLowerInvariant();
    }

    private static String PeekBinaryMagic(BinaryReader reader) {
      if (reader.BaseStream.Length < 0x1C) return String.Empty;

      UInt32 length = reader.ReadUInt32();
      if (length == 0 || length > 256 || reader.BaseStream.Position + length > reader.BaseStream.Length)
        return String.Empty;

      return Encoding.ASCII.GetString(reader.ReadBytes((Int32)length)).TrimEnd('\0');
    }

    #region Area DAT
    private static ArrayList ParseAreaBinary(BinaryReader br) {
      ValidateMagic(br, "AREA_DAT_BINARY_FORMAT_");

      br.BaseStream.Position = 0x1C;
      UInt32 roomsOffset = br.ReadUInt32();
      UInt32 assetsOffset = br.ReadUInt32();
      UInt32 pathsOffset = br.ReadUInt32();
      UInt32 schemesOffset = br.ReadUInt32();
      UInt32 terrainOffset = br.ReadUInt32();
      UInt32 dydTexturesOffset = br.ReadUInt32();
      UInt32 dydChannelsOffset = br.ReadUInt32();
      UInt32 settingsOffset = br.ReadUInt32();
      UInt32 guidOffset = br.ReadUInt32();

      ArrayList roots = new ArrayList();

      // Settings are shown first, matching Jedipedia's area reader.
      NodeListItem settingsRoot = Branch("Settings");
      settingsRoot.children.Add(Leaf("Format", "Binary"));
      settingsRoot.children.Add(Leaf("GUID section offset", Hex(guidOffset)));

      br.BaseStream.Position = settingsOffset;
      UInt32 envCount = ReadCount(br, "environment materials");
      NodeListItem envRoot = Branch($"Environment Materials ({envCount:N0})");
      for (UInt32 i = 0; i < envCount; i++) {
        UInt32 id = br.ReadUInt32();
        String name = ReadSizedAscii(br);
        envRoot.children.Add(Leaf(id.ToString(Invariant), name));
      }

      UInt64 areaId = br.ReadUInt64();
      String areaName = ReadSizedAscii(br);
      Int32 skyRotation = br.ReadInt32();
      UInt64 skydome = br.ReadUInt64();
      Single[] unknownZeros = new Single[6];
      for (Int32 i = 0; i < unknownZeros.Length; i++) unknownZeros[i] = br.ReadSingle();
      UInt32 unkInt1 = br.ReadUInt32();
      UInt32 unkInt2 = br.ReadUInt32();

      settingsRoot.children.Add(Leaf("Area ID", areaId.ToString(Invariant)));
      settingsRoot.children.Add(Leaf("Area Name", areaName));
      settingsRoot.children.Add(Leaf("Sky Rotation", skyRotation.ToString(Invariant)));
      settingsRoot.children.Add(Leaf("Skydome", skydome.ToString(Invariant)));
      settingsRoot.children.Add(Leaf("Unknown Int 1", unkInt1.ToString(Invariant)));
      settingsRoot.children.Add(Leaf("Unknown Int 2", unkInt2.ToString(Invariant)));
      roots.Add(settingsRoot);

      br.BaseStream.Position = roomsOffset;
      UInt32 roomCount = ReadCount(br, "rooms");
      NodeListItem roomsRoot = Branch($"Rooms ({roomCount:N0})");
      for (UInt32 i = 0; i < roomCount; i++) {
        String room = ReadSizedAscii(br);
        roomsRoot.children.Add(Leaf(room, String.Empty));
      }
      roots.Add(roomsRoot);

      br.BaseStream.Position = assetsOffset;
      UInt32 assetCount = ReadCount(br, "assets");
      NodeListItem assetsRoot = Branch($"Assets ({assetCount:N0})");
      for (UInt32 i = 0; i < assetCount; i++) {
        UInt64 id = br.ReadUInt64();
        String name = ReadSizedAscii(br);
        assetsRoot.children.Add(Leaf(id.ToString(Invariant), name));
      }
      roots.Add(assetsRoot);

      br.BaseStream.Position = pathsOffset;
      UInt32 pathCount = ReadCount(br, "paths");
      NodeListItem pathsRoot = Branch($"Paths ({pathCount:N0})");
      for (UInt32 i = 0; i < pathCount; i++) {
        UInt64 id = br.ReadUInt64();
        String name = ReadSizedAscii(br);
        String fqn = ReadSizedAscii(br);
        Single r = br.ReadSingle();
        Single g = br.ReadSingle();
        Single b = br.ReadSingle();
        Single a = br.ReadSingle();
        Boolean circular = br.ReadByte() != 0;
        Boolean cardinal = br.ReadByte() != 0;
        UInt32 pointCount = ReadCount(br, "path points");

        NodeListItem path = Branch($"{name} ({id})", fqn);
        path.children.Add(Leaf("Color", Vec4(r, g, b, a)));
        path.children.Add(Leaf("Circular", circular.ToString()));
        path.children.Add(Leaf("Cardinal", cardinal.ToString()));
        NodeListItem points = Branch($"Points ({pointCount:N0})");
        for (UInt32 j = 0; j < pointCount; j++) {
          UInt64 pointPathId = br.ReadUInt64();
          UInt64 pointId = br.ReadUInt64();
          Single px = br.ReadSingle();
          Single py = br.ReadSingle();
          Single pz = br.ReadSingle();
          Single rx = br.ReadSingle();
          Single ry = br.ReadSingle();
          Single rz = br.ReadSingle();
          String data = ReadSizedAscii(br);

          NodeListItem point = Branch(pointId.ToString(Invariant));
          point.children.Add(Leaf("Path ID", pointPathId.ToString(Invariant)));
          point.children.Add(Leaf("Position", Vec3(px, py, pz)));
          point.children.Add(Leaf("Rotation", Vec3(rx, ry, rz)));
          if (!String.IsNullOrEmpty(data)) point.children.Add(Leaf("Data", data));
          points.children.Add(point);
        }
        path.children.Add(points);
        pathsRoot.children.Add(path);
      }
      roots.Add(pathsRoot);

      br.BaseStream.Position = schemesOffset;
      UInt32 schemeCount = ReadCount(br, "environment schemes");
      NodeListItem schemesRoot = Branch($"Schemes ({schemeCount:N0})");
      for (UInt32 i = 0; i < schemeCount; i++) {
        String name = ReadSizedAscii(br);
        String data = ReadSizedAscii(br);
        NodeListItem scheme = Branch(name);
        foreach (KeyValuePair<String, String> item in ParseSchemeData(data))
          scheme.children.Add(Leaf(item.Key, item.Value));
        schemesRoot.children.Add(scheme);
      }
      roots.Add(schemesRoot);

      br.BaseStream.Position = terrainOffset;
      UInt32 terrainCount = ReadCount(br, "terrain textures");
      NodeListItem terrainRoot = Branch($"Terrain Textures ({terrainCount:N0})");
      for (UInt32 i = 0; i < terrainCount; i++) {
        UInt32 id = br.ReadUInt32();
        UInt32 layer = br.ReadUInt32();
        String name = ReadSizedAscii(br);
        terrainRoot.children.Add(Leaf(id.ToString(Invariant), $"{name}  [layer {layer}]"));
      }
      roots.Add(terrainRoot);

      br.BaseStream.Position = dydTexturesOffset;
      UInt32 dydTextureCount = ReadCount(br, "dynamic detail textures");
      NodeListItem dydTexturesRoot = Branch($"DYD Textures ({dydTextureCount:N0})");
      for (UInt32 i = 0; i < dydTextureCount; i++) {
        UInt32 id = br.ReadUInt32();
        String name = ReadSizedAscii(br);
        dydTexturesRoot.children.Add(Leaf(id.ToString(Invariant), name));
      }
      roots.Add(dydTexturesRoot);

      br.BaseStream.Position = dydChannelsOffset;
      UInt32 dydChannelCount = ReadCount(br, "dynamic detail channels");
      NodeListItem dydChannelsRoot = Branch($"DYD Channels ({dydChannelCount:N0})");
      for (UInt32 i = 0; i < dydChannelCount; i++) {
        UInt32 type = br.ReadUInt32();
        UInt32 id = br.ReadUInt32();
        NodeListItem channel = Branch(id.ToString(Invariant), $"type {type}");
        switch (type) {
          case 0:
            for (Int32 j = 0; j < 15; j++)
              channel.children.Add(Leaf($"Value {j}", br.ReadUInt32().ToString(Invariant)));
            break;
          case 2:
            channel.children.Add(Leaf("Value", br.ReadInt32().ToString(Invariant)));
            break;
          case 3:
            channel.children.Add(Leaf("String", ReadSizedAscii(br)));
            for (Int32 j = 0; j < 7; j++)
              channel.children.Add(Leaf($"Float {j}", Float(br.ReadSingle())));
            UInt32 materialCount = ReadCount(br, "DYD materials");
            NodeListItem materials = Branch($"Materials ({materialCount:N0})");
            for (UInt32 j = 0; j < materialCount; j++) {
              UInt32 matId = br.ReadUInt32();
              Single unkFloat = br.ReadSingle();
              String matName = ReadSizedAscii(br);
              Byte unkBool1 = br.ReadByte();
              Single[] unkFloats = new Single[6];
              for (Int32 k = 0; k < 6; k++) unkFloats[k] = br.ReadSingle();
              Byte unkBool2 = br.ReadByte();
              Single unkFloat2 = br.ReadSingle();
              UInt32 unkId = br.ReadUInt32();
              Byte unkBool3 = br.ReadByte();

              NodeListItem material = Branch(matId.ToString(Invariant), matName);
              material.children.Add(Leaf("Unknown Float", Float(unkFloat)));
              material.children.Add(Leaf("Unknown Bool 1", (unkBool1 != 0).ToString()));
              material.children.Add(Leaf("Unknown Floats", String.Join(", ", unkFloats.Select(Float))));
              material.children.Add(Leaf("Unknown Bool 2", (unkBool2 != 0).ToString()));
              material.children.Add(Leaf("Unknown Float 2", Float(unkFloat2)));
              material.children.Add(Leaf("Unknown ID", unkId.ToString(Invariant)));
              material.children.Add(Leaf("Unknown Bool 3", (unkBool3 != 0).ToString()));
              materials.children.Add(material);
            }
            channel.children.Add(materials);
            break;
          default:
            throw new InvalidDataException($"Unknown area.dat DYD channel type {type} at {Hex(br.BaseStream.Position)}.");
        }
        dydChannelsRoot.children.Add(channel);
      }
      roots.Add(dydChannelsRoot);

      roots.Add(envRoot);
      return roots;
    }

    private static ArrayList ParseAreaText(String text) {
      String[] lines = NormalizeLines(text);
      ArrayList roots = new ArrayList();
      NodeListItem settings = Branch("Settings");
      settings.children.Add(Leaf("Format", "Text"));

      NodeListItem rooms = Branch("Rooms");
      NodeListItem assets = Branch("Assets");
      NodeListItem paths = Branch("Paths");
      NodeListItem schemes = Branch("Schemes");
      NodeListItem terrain = Branch("Terrain Textures");
      NodeListItem dydTextures = Branch("DYD Textures");
      NodeListItem dydChannels = Branch("DYD Channels");

      String section = String.Empty;
      Dictionary<String, NodeListItem> pathById = new Dictionary<String, NodeListItem>();
      Regex addPath = new Regex(
        "^addpath \\\"([^\\\"]+)\\\" \\\"([^\\\"]*)\\\" \\\"([^\\\"]*)\\\" \\\"([^\\\"]*)\\\" \\\"(false|true)\\\" \\\"(false|true)\\\"$",
        RegexOptions.IgnoreCase
      );
      Regex addPoint = new Regex(
        "^addpoint \\\"([^\\\"]+)\\\" \\\"([^\\\"]+)\\\" \\(([^,]+),([^,]+),([^\\)]+)\\) \\(([^,]+),([^,]+),([^\\)]+)\\) \\\"(.*)\\\"$",
        RegexOptions.IgnoreCase
      );

      foreach (String original in lines) {
        String line = original.Trim();
        if (line.Length == 0 || line == "!") continue;
        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) {
          section = line.ToUpperInvariant();
          continue;
        }

        switch (section) {
          case "[ROOMS]":
            rooms.children.Add(Leaf(line, String.Empty));
            break;
          case "[ASSETS]": {
            Int32 pos = line.IndexOf('=');
            if (pos > 0)
              assets.children.Add(Leaf(line.Substring(0, pos).Trim(), line.Substring(pos + 1).Trim()));
            break;
          }
          case "[PATHS]": {
            Match m = addPath.Match(line);
            if (m.Success) {
              NodeListItem path = Branch($"{m.Groups[2].Value} ({m.Groups[1].Value})", m.Groups[4].Value);
              if (!String.IsNullOrWhiteSpace(m.Groups[3].Value)) path.children.Add(Leaf("Color", m.Groups[3].Value));
              path.children.Add(Leaf("Circular", m.Groups[5].Value));
              path.children.Add(Leaf("Cardinal", m.Groups[6].Value));
              NodeListItem points = Branch("Points");
              path.children.Add(points);
              paths.children.Add(path);
              pathById[m.Groups[1].Value] = path;
              break;
            }
            m = addPoint.Match(line);
            if (m.Success && pathById.TryGetValue(m.Groups[1].Value, out NodeListItem parentPath)) {
              NodeListItem point = Branch(m.Groups[2].Value);
              point.children.Add(Leaf("Position", $"({m.Groups[3].Value}, {m.Groups[4].Value}, {m.Groups[5].Value})"));
              point.children.Add(Leaf("Rotation", $"({m.Groups[6].Value}, {m.Groups[7].Value}, {m.Groups[8].Value})"));
              if (!String.IsNullOrWhiteSpace(m.Groups[9].Value)) point.children.Add(Leaf("Data", m.Groups[9].Value));
              parentPath.children.Last().children.Add(point);
            }
            break;
          }
          case "[SCHEMES]": {
            Match m = Regex.Match(line, "^\\\"([^\\\"]+)\\\"\\s+(~0\\|.*)$");
            if (m.Success) {
              NodeListItem scheme = Branch(m.Groups[1].Value);
              foreach (KeyValuePair<String, String> pair in ParseSchemeData(m.Groups[2].Value))
                scheme.children.Add(Leaf(pair.Key, pair.Value));
              schemes.children.Add(scheme);
            }
            break;
          }
          case "[TERRAINTEXTURES]": {
            Int32 colon = line.IndexOf(':');
            if (colon > 0)
              terrain.children.Add(Leaf(line.Substring(0, colon), line.Substring(colon + 1)));
            break;
          }
          case "[DYDTEXTURES]": {
            Int32 colon = line.IndexOf(':');
            if (colon > 0)
              dydTextures.children.Add(Leaf(line.Substring(0, colon), line.Substring(colon + 1)));
            break;
          }
          case "[DYDCHANNELPARAMS]": {
            Int32 colon = line.IndexOf(':');
            if (colon > 0)
              dydChannels.children.Add(Leaf(line.Substring(0, colon), line.Substring(colon + 1)));
            break;
          }
          case "[SETTINGS]": {
            Int32 equals = line.IndexOf('=');
            if (equals > 0)
              settings.children.Add(Leaf(line.Substring(0, equals).Trim(), line.Substring(equals + 1).Trim()));
            break;
          }
        }
      }


      // NodeListItem names are immutable, so create count-labelled roots for the result.
      roots.Add(settings);
      roots.Add(WithCount("Rooms", rooms));
      roots.Add(WithCount("Assets", assets));
      roots.Add(WithCount("Paths", paths));
      roots.Add(WithCount("Schemes", schemes));
      roots.Add(WithCount("Terrain Textures", terrain));
      roots.Add(WithCount("DYD Textures", dydTextures));
      roots.Add(WithCount("DYD Channels", dydChannels));
      return roots;
    }
    #endregion

    #region Room DAT
    private static ArrayList ParseRoomBinary(BinaryReader br, Dictionary<UInt64, String> areaAssets) {
      ValidateMagic(br, "ROOM_DAT_BINARY_FORMAT_");

      br.BaseStream.Position = 0x1C;
      UInt32 instancesOffset = br.ReadUInt32();
      UInt32 visibleOffset = br.ReadUInt32();
      UInt32 settingsOffset = br.ReadUInt32();

      br.BaseStream.Position = 0x30;
      String sourceFile = ReadSizedAscii(br);

      ArrayList roots = new ArrayList();
      NodeListItem settings = Branch("Settings");
      settings.children.Add(Leaf("Format", "Binary"));
      settings.children.Add(Leaf("Source File", sourceFile));

      br.BaseStream.Position = settingsOffset;
      String envScheme = ReadSizedUtf16(br);
      String maps = ReadSizedUtf16(br);
      String mapsVisible = ReadSizedUtf16(br);
      Single minX = br.ReadSingle();
      Single minY = br.ReadSingle();
      Single minZ = br.ReadSingle();
      Single maxX = br.ReadSingle();
      Single maxY = br.ReadSingle();
      Single maxZ = br.ReadSingle();
      UInt64 roomId = br.ReadUInt64();
      Boolean outdoorsVisible = br.ReadByte() != 0;
      Boolean disablePlantEmitters = br.ReadByte() != 0;

      settings.children.Add(Leaf("Environment Scheme", envScheme));
      settings.children.Add(Leaf("Maps", maps));
      settings.children.Add(Leaf("Maps Visible", mapsVisible));
      settings.children.Add(Leaf("Bounding Box Min", Vec3(minX, minY, minZ)));
      settings.children.Add(Leaf("Bounding Box Max", Vec3(maxX, maxY, maxZ)));
      settings.children.Add(Leaf("Room ID", roomId.ToString(Invariant)));
      settings.children.Add(Leaf("Outdoors Visible", outdoorsVisible.ToString()));
      settings.children.Add(Leaf("Disable Plant Emitters", disablePlantEmitters.ToString()));

      br.BaseStream.Position = visibleOffset;
      UInt32 visibleCount = ReadCount(br, "visible rooms");
      NodeListItem visible = Branch($"Visible ({visibleCount:N0})");
      for (UInt32 i = 0; i < visibleCount; i++)
        visible.children.Add(Leaf(ReadSizedUtf16(br), String.Empty));

      br.BaseStream.Position = instancesOffset;
      UInt32 instanceCount = ReadCount(br, "room instances");
      NodeListItem instances = Branch($"Instances ({instanceCount:N0})");
      for (UInt32 i = 0; i < instanceCount; i++) {
        EnsureRemaining(br, 5 + 8 + 8 + 1 + 4 + 4 + 1, "room instance header");
        br.BaseStream.Seek(5, SeekOrigin.Current);
        UInt64 instanceId = br.ReadUInt64();
        UInt64 assetId = br.ReadUInt64();
        br.ReadByte();
        UInt32 propertyCount = ReadCount(br, "room properties");
        UInt32 propertiesLength = br.ReadUInt32();
        Int64 propertiesEnd = checked(br.BaseStream.Position + propertiesLength);
        br.ReadByte();

        String assetName = areaAssets != null && areaAssets.TryGetValue(assetId, out String resolved)
          ? resolved
          : "Asset " + assetId.ToString(Invariant);
        // Jedipedia's default room view puts the resolved asset name front-and-centre
        // and hides the instance id by default. Mirror that emphasis in the two-column
        // Asset Browser tree while keeping both ids immediately available.
        NodeListItem instance = Branch(assetName, instanceId.ToString(Invariant));
        instance.children.Add(Leaf("Instance ID", instanceId.ToString(Invariant)));
        instance.children.Add(Leaf("Asset ID", assetId.ToString(Invariant)));

        for (UInt32 j = 0; j < propertyCount; j++) {
          Byte type = br.ReadByte();
          UInt32 id = br.ReadUInt32();
          String value = ReadRoomPropertyValue(br, type);
          RoomProperties.TryGetValue(id, out RoomPropertyInfo info);
          String name = info?.Name ?? $"0x{id:X8}";
          // The type byte stored in the room file is authoritative. The Jedipedia
          // metadata table is used for the friendly property name only because a
          // small number of properties legitimately occur with more than one type.
          String typeName = RoomTypeName(type);
          instance.children.Add(Leaf($"{name} (0x{id:X8})", $"{value}  [{typeName}]"));
        }

        if (propertiesEnd > br.BaseStream.Length)
          throw new InvalidDataException("Room property block extends beyond the end of the file.");
        if (br.BaseStream.Position > propertiesEnd)
          throw new InvalidDataException("Room property parser read past the declared property block.");
        br.BaseStream.Position = propertiesEnd;
        instances.children.Add(instance);
      }

      // Jedipedia opens room DATs on the instances view by default. Keep the same
      // ordering here; settings and the visible-room list remain one click away.
      roots.Add(instances);
      roots.Add(settings);
      roots.Add(visible);
      return roots;
    }

    private static ArrayList ParseRoomText(String text, Dictionary<UInt64, String> areaAssets) {
      String[] lines = NormalizeLines(text);
      ArrayList roots = new ArrayList();
      NodeListItem settings = Branch("Settings");
      settings.children.Add(Leaf("Format", "Text"));
      NodeListItem visible = Branch("Visible");
      NodeListItem instances = Branch("Instances");
      NodeListItem current = null;
      String section = String.Empty;

      foreach (String original in lines) {
        String trimmed = original.Trim();
        if (trimmed.Length == 0 || trimmed == "!") continue;
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)) {
          section = trimmed.ToUpperInvariant();
          continue;
        }

        switch (section) {
          case "[INSTANCES]":
            if (original.StartsWith("  4", StringComparison.Ordinal)) {
              Int32 equals = trimmed.IndexOf('=');
              if (equals > 0) {
                String instanceId = trimmed.Substring(0, equals);
                String assetIdText = trimmed.Substring(equals + 1);
                UInt64.TryParse(assetIdText, out UInt64 assetId);
                String assetName = areaAssets != null && areaAssets.TryGetValue(assetId, out String resolved)
                  ? resolved
                  : "Asset " + assetIdText;
                current = Branch(assetName, instanceId);
                current.children.Add(Leaf("Instance ID", instanceId));
                current.children.Add(Leaf("Asset ID", assetIdText));
                instances.children.Add(current);
              }
            } else if (original.StartsWith("    .", StringComparison.Ordinal) && current != null) {
              Int32 equals = original.IndexOf('=');
              if (equals > 5) {
                String name = original.Substring(5, equals - 5).Trim();
                String value = original.Substring(equals + 1).Trim();
                current.children.Add(Leaf(name, value));
              }
            }
            break;
          case "[VISIBLE]":
            visible.children.Add(Leaf(trimmed, String.Empty));
            break;
          case "[SETTINGS]": {
            Int32 equals = trimmed.IndexOf('=');
            if (equals > 0)
              settings.children.Add(Leaf(trimmed.Substring(0, equals), trimmed.Substring(equals + 1)));
            break;
          }
        }
      }

      roots.Add(WithCount("Instances", instances));
      roots.Add(settings);
      roots.Add(WithCount("Visible", visible));
      return roots;
    }

    private static String ReadRoomPropertyValue(BinaryReader br, Byte type) {
      switch (type) {
        case 0:
          return (br.ReadByte() != 0).ToString();
        case 1:
          return br.ReadUInt32().ToString(Invariant);
        case 3:
          return br.ReadUInt32().ToString(Invariant);
        case 4:
          return Float(br.ReadSingle());
        case 5:
          return br.ReadUInt64().ToString(Invariant);
        case 6:
          return Vec3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        case 7:
          return Vec4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        case 8:
          return ReadSizedUtf16(br);
        case 9: {
          UInt32 length = br.ReadUInt32();
          EnsureRemaining(br, length, "compressed room property");
          String codec = "unknown";
          if (length >= 4) {
            Int64 pos = br.BaseStream.Position;
            Byte[] magic = br.ReadBytes((Int32)Math.Min(length, 4u));
            br.BaseStream.Position = pos;
            if (magic.Length >= 4 && magic[0] == 0x28 && magic[1] == 0xB5
                && magic[2] == 0x2F && magic[3] == 0xFD)
              codec = "zstd";
            else if (magic.Length >= 2 && magic[0] == 0x78 && magic[1] == 0x9C)
              codec = "zlib";
            else if (magic.Length >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
              codec = "gzip";
          }
          br.BaseStream.Seek(length, SeekOrigin.Current);
          return $"[{length:N0} compressed bytes; {codec}]";
        }
        default:
          throw new InvalidDataException($"Unknown room DAT property type {type}.");
      }
    }
    #endregion

    #region Shared Helpers
    private static Dictionary<UInt64, String> TryLoadAreaAssetMap(Assets assets, String directory) {
      Dictionary<UInt64, String> result = new Dictionary<UInt64, String>();
      if (assets == null || String.IsNullOrWhiteSpace(directory)) return result;

      try {
        String path = directory.TrimEnd('/') + "/area.dat";
        TorArchive.File areaFile = assets.FindFile(path);
        if (areaFile == null) return result;

        using Stream stream = areaFile.OpenCopyInMemory();
        using BinaryReader br = new BinaryReader(stream, Encoding.UTF8, true);
        String magic = PeekBinaryMagic(br);
        br.BaseStream.Position = 0;

        if (String.Equals(magic, "AREA_DAT_BINARY_FORMAT_", StringComparison.Ordinal)) {
          br.BaseStream.Position = 0x20;
          UInt32 assetsOffset = br.ReadUInt32();
          br.BaseStream.Position = assetsOffset;
          UInt32 count = ReadCount(br, "area assets");
          for (UInt32 i = 0; i < count; i++) {
            UInt64 id = br.ReadUInt64();
            result[id] = ReadSizedAscii(br);
          }
          return result;
        }

        br.BaseStream.Position = 0;
        using StreamReader sr = new StreamReader(br.BaseStream, Encoding.UTF8, true, 4096, true);
        String text = sr.ReadToEnd();
        String section = String.Empty;
        foreach (String original in NormalizeLines(text)) {
          String line = original.Trim();
          if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) {
            section = line.ToUpperInvariant();
            continue;
          }
          if (section != "[ASSETS]") continue;
          Int32 equals = line.IndexOf('=');
          if (equals <= 0) continue;
          if (UInt64.TryParse(line.Substring(0, equals).Trim(), out UInt64 id))
            result[id] = line.Substring(equals + 1).Trim();
        }
      }
      catch {
        // Room preview remains useful without area asset-name resolution.
      }
      return result;
    }

    private static void ValidateMagic(BinaryReader br, String expected) {
      br.BaseStream.Position = 0;
      UInt32 length = br.ReadUInt32();
      String actual = Encoding.ASCII.GetString(br.ReadBytes((Int32)length)).TrimEnd('\0');
      if (!String.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidDataException($"Expected DAT magic '{expected}', got '{actual}'.");
    }

    private static UInt32 ReadCount(BinaryReader br, String section) {
      EnsureRemaining(br, 4, section);
      UInt32 count = br.ReadUInt32();
      if (count > 5_000_000)
        throw new InvalidDataException($"Unreasonable {section} count: {count:N0}.");
      return count;
    }

    private static String ReadSizedAscii(BinaryReader br) {
      EnsureRemaining(br, 4, "string length");
      UInt32 length = br.ReadUInt32();
      EnsureRemaining(br, length, "ASCII string");
      if (length > Int32.MaxValue) throw new InvalidDataException("String is too large.");
      return Encoding.ASCII.GetString(br.ReadBytes((Int32)length)).TrimEnd('\0');
    }

    private static String ReadSizedUtf16(BinaryReader br) {
      EnsureRemaining(br, 4, "UTF-16 string length");
      UInt32 length = br.ReadUInt32();
      EnsureRemaining(br, length, "UTF-16 string");
      if (length > Int32.MaxValue) throw new InvalidDataException("String is too large.");
      Byte[] bytes = br.ReadBytes((Int32)length);
      return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static void EnsureRemaining(BinaryReader br, UInt64 bytes, String context) {
      if (bytes > (UInt64)Math.Max(0, br.BaseStream.Length - br.BaseStream.Position))
        throw new EndOfStreamException($"Unexpected end of DAT while reading {context}.");
    }

    private static Dictionary<String, String> ParseSchemeData(String data) {
      Dictionary<String, String> result = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
      if (String.IsNullOrEmpty(data)) return result;
      if (!data.StartsWith("~0|", StringComparison.Ordinal)) {
        result["Raw Data"] = data;
        return result;
      }

      try {
        Int32 pos = 3;
        Int32 parameterCount = ReadPipeInt(data, ref pos);
        for (Int32 i = 0; i < parameterCount; i++) {
          Int32 keyLength = ReadPipeInt(data, ref pos);
          if (pos + keyLength > data.Length) throw new InvalidDataException();
          String key = data.Substring(pos, keyLength);
          pos += keyLength;
          ExpectPipe(data, ref pos);

          Int32 valueLength = ReadPipeInt(data, ref pos);
          if (pos + valueLength > data.Length) throw new InvalidDataException();
          String value = data.Substring(pos, valueLength);
          pos += valueLength;
          ExpectPipe(data, ref pos);
          result[key] = value;
        }
      }
      catch {
        result.Clear();
        result["Raw Data"] = data;
      }
      return result;
    }

    private static Int32 ReadPipeInt(String data, ref Int32 pos) {
      Int32 start = pos;
      while (pos < data.Length && data[pos] != '|') pos++;
      if (pos >= data.Length) throw new InvalidDataException();
      String number = data.Substring(start, pos - start);
      pos++;
      return Int32.Parse(number, NumberStyles.Integer, Invariant);
    }

    private static void ExpectPipe(String data, ref Int32 pos) {
      if (pos >= data.Length || data[pos] != '|') throw new InvalidDataException();
      pos++;
    }

    private static String[] NormalizeLines(String text) {
      return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static NodeListItem Leaf(String name, String value) =>
      new NodeListItem(name ?? String.Empty, value ?? String.Empty);

    private static NodeListItem Branch(String name, String value = "") =>
      new NodeListItem(name ?? String.Empty, value ?? String.Empty);

    private static NodeListItem WithCount(String name, NodeListItem source) {
      NodeListItem root = Branch($"{name} ({source.children.Count:N0})");
      root.children.AddRange(source.children);
      return root;
    }

    private static String RoomTypeName(Byte type) {
      return type switch {
        0 => "bool",
        1 => "int32",
        3 => "enum",
        4 => "float",
        5 => "id",
        6 => "vector3",
        7 => "vector4",
        8 => "string",
        9 => "binary",
        _ => "type " + type.ToString(Invariant)
      };
    }

    private static String Float(Single value) => value.ToString("0.#####", Invariant);
    private static String Vec3(Single x, Single y, Single z) =>
      $"({Float(x)}, {Float(y)}, {Float(z)})";
    private static String Vec4(Single x, Single y, Single z, Single w) =>
      $"({Float(x)}, {Float(y)}, {Float(z)}, {Float(w)})";
    private static String Hex(UInt32 value) => "0x" + value.ToString("X", Invariant);
    private static String Hex(Int64 value) => "0x" + value.ToString("X", Invariant);
    #endregion
  }
}
