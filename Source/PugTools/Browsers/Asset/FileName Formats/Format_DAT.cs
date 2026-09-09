using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace PugTools {
  internal class Format_DAT {
    private readonly HashSet<String> _animFileNames;
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;
    private String _filename;
    private readonly Dictionary<UInt32, DatTypeId> _properties;

    internal HashSet<String> FileNames { get; set; }

    internal Format_DAT(String dest, String ext) {
      _animFileNames = new HashSet<String>();
      // _checkKeys = new HashSet<String>(
      //   new String[] {
      //     ".NormalMap2",
      //     ".NormalMap1",
      //     ".SurfaceMap",
      //     ".RampMap",
      //     ".Falloff",
      //     ".IlluminationMap",
      //     ".FxSpecName",
      //     ".EnvironmentMap",
      //     ".Intensity",
      //     ".PortalTarget",
      //     ".Color",
      //     ".gfxMovieName",
      //     ".DiffuseColor",
      //     ".ProjectionTexture"
      //   }
      // );
      _dest = dest;
      _errors = new List<String>();
      _extension = ext;
      // _portalTargets = new HashSet<String>();
      _properties = new Dictionary<UInt32, DatTypeId>();
      FileNames = new HashSet<String>();
    }
    internal void ParseDAT(Stream fileStream, String fullFileName, AssetBrowser form) {
      if (form == null) throw new ArgumentNullException(nameof(form));

      _filename = fullFileName;
      Boolean oldFormat = true;
      using BinaryReader br = new BinaryReader(fileStream);
      Int32 header = br.ReadInt32();

      if (header == 24) {
        oldFormat = false;
        // fileStream.Position = 4;
        Char c = br.ReadChar();
        StringBuilder formatter = new StringBuilder();

        while (c != '\0') {
          formatter.Append(c);
          c = br.ReadChar();
        }

        String format = formatter.ToString();

        switch (format) {
          case "ROOM_DAT_BINARY_FORMAT_":
            ParseRoomDAT(br);
            break;

          case "AREA_DAT_BINARY_FORMAT_":
            ParseAreaDAT(br);
            break;

          default:
            break;
        }
      }

      if (oldFormat) {
        if (fileStream.CanSeek) fileStream.Position = 0;
        else {
          // string soin = ""; //dunno what to do here
        }

        StreamReader reader = new StreamReader(fileStream);
        String stream_line;
        List<String> stream_lines = new List<String>();

        while ((stream_line = reader.ReadLine()) != null) {
          stream_lines.Add(stream_line.TrimStart());
        }

        reader.Close();

        if (stream_lines.Any(x => x.Contains("! Area Specification")))
          ParseAreaDAT(stream_lines);
        else if (stream_lines.Any(x => x.Contains("! Room Specification")))
          ParseRoomDAT(stream_lines);
        else if (stream_lines.Any(x => x.Contains("! Character Specification")))
          ParseCharacterDAT(stream_lines);
        else
          //throw new Exception("Unknown DAT Specification" + stream_lines[1]);
          Debug.WriteLine("Unknown DAT Specification" + stream_lines[1]);
      }
    }

    #region New Format Readers
    internal void ParseAreaDAT(BinaryReader br) {
      br.BaseStream.Position = 0x1C; //Skip room header

      UInt32 roomOffset = br.ReadUInt32();
      UInt32 assetsOffset = br.ReadUInt32();
      br.ReadUInt32();
      UInt32 schemesOffset = br.ReadUInt32();
      UInt32 terTexOffset = br.ReadUInt32();
      UInt32 DydTexOffset = br.ReadUInt32();
      br.ReadUInt32();
      br.ReadUInt32();

      UInt32 guidOffset = br.ReadUInt32();
      br.ReadBytes(0x16); //Always (01 00) repeating

      br.BaseStream.Position = guidOffset;
      UInt64 areaGuid = br.ReadUInt64();

      // The path segment is more authoritative than the GUID stored inside the file. Retail
      // live-content worlds use /resources/world/livecontent/systemgenerated/<id>/area.dat,
      // while ordinary worlds live below /resources/world/areas/<id>. Keep the actual parent
      // directory so room/mapnote candidates stay in the same namespace instead of being moved
      // back under /world/areas (Jedipedia also resolves rooms relative to the loaded area.dat).
      String areaDirectory = null;
      String normalizedFileName = (_filename ?? String.Empty).Replace('\\', '/').ToLowerInvariant();
      if (normalizedFileName.EndsWith("/area.dat", StringComparison.Ordinal)) {
        String parent = normalizedFileName.Substring(0, normalizedFileName.Length - "/area.dat".Length);
        if (parent.StartsWith("/resources/world/areas/", StringComparison.Ordinal)
            || parent.StartsWith("/resources/world/livecontent/systemgenerated/", StringComparison.Ordinal)) {
          areaDirectory = parent;
          FileNames.Add(areaDirectory + "/mapnotes.not");
        }
      }

      //Rooms
      br.BaseStream.Position = roomOffset;
      UInt32 numRooms = br.ReadUInt32();

      for (UInt32 i = 0; i < numRooms; i++) {
        UInt32 nameLength = br.ReadUInt32();
        String room = ReadString(br, nameLength).ToLower();

        if (areaDirectory != null)
          FileNames.Add(areaDirectory + "/" + room + ".dat");
        else
          FileNames.Add(String.Format("/resources/world/areas/{0}/{1}.dat", areaGuid, room));
      }

      //Assets
      br.BaseStream.Position = assetsOffset;
      UInt32 numAssets = br.ReadUInt32();

      for (UInt32 i = 0; i < numAssets; i++) {
        br.ReadUInt64();
        UInt32 nameLength = br.ReadUInt32();
        String assetName = ReadString(br, nameLength);

        if (assetName.Contains(':') || assetName.Contains('#')) continue;

        FileNames.Add("/resources" + assetName.ToLower().Replace("\\", "/"));
      }

      //Paths

      //Schemes
      br.BaseStream.Position = schemesOffset;
      UInt32 numSchemes = br.ReadUInt32();

      for (UInt32 i = 0; i < numSchemes; i++) {
        UInt32 nameLength = br.ReadUInt32();
        ReadString(br, nameLength);
        UInt32 schemeLength = br.ReadUInt32();
        String scheme = ReadString(br, schemeLength);

        if (scheme.Contains("/")) {
          Int32 idx = 0;

          while ((idx = scheme.IndexOf('/', idx)) != -1) {
            Int32 end = scheme.IndexOf('|', idx);
            Int32 len = end - idx;
            String final = scheme.Substring(idx, len).ToLower();
            FileNames.Add(String.Format("/resources{0}.tex", final));
            FileNames.Add(String.Format("/resources{0}.dds", final));
            FileNames.Add(String.Format("/resources{0}.tiny.dds", final));
            idx = end;
          }
        }
      }

      //TERRAINTEXTURES
      br.BaseStream.Position = terTexOffset;
      UInt32 numTerTex = br.ReadUInt32();

      for (UInt32 i = 0; i < numTerTex; i++) {
        br.ReadUInt64();
        UInt32 nameLength = br.ReadUInt32();
        String terTexName = ReadString(br, nameLength);

        FileNames.Add(
          String.Format("/resources/art/shaders/materials/{0}.mat", terTexName.ToLower()));
        FileNames.Add(
          String.Format(
            "/resources/art/shaders/environmentmaterials/{0}.emt",
            terTexName.ToLower()
          )
        );
      }

      //TERRAINTEXTURES
      br.BaseStream.Position = DydTexOffset;
      UInt32 numDydTex = br.ReadUInt32();

      for (UInt32 i = 0; i < numDydTex; i++) {
        br.ReadUInt32();
        UInt32 nameLength = br.ReadUInt32();
        String terTexName = ReadString(br, nameLength);

        FileNames.Add(
          String.Format("/resources/art/shaders/materials/{0}.mat", terTexName.ToLower())
        );
        FileNames.Add(
          String.Format(
            "/resources/art/shaders/environmentmaterials/{0}.emt",
            terTexName.ToLower()
          )
        );
      }

      //DYDCHANNELPARAMS

      //SETTINGS
    }

    private static String ReadString(BinaryReader br, UInt32 length) {
      Int64 curpos = br.BaseStream.Position;
      Int64 endpos = curpos + length;
      Char c = br.ReadChar();
      StringBuilder builder = new StringBuilder();

      while (c != '\0' && br.BaseStream.Position < endpos) {
        builder.Append(c);
        c = br.ReadChar();
      }

      return builder.ToString();
    }

    internal void ParseRoomDAT(BinaryReader br) {
      HashSet<String> fxspecs = new HashSet<String>();
      HashSet<String> textures = new HashSet<String>();

      br.BaseStream.Position = 0x1C; //Skip room header

      UInt32 instanceOffset = br.ReadUInt32();
      br.ReadUInt32();
      br.ReadUInt32();
      br.ReadUInt64(); //Always 281479271743491 : (03 00 01 00 01 00 01 00)

      UInt32 fileNameLength = br.ReadUInt32();
      String filename = ReadString(br, fileNameLength);
      FileNames.Add(string.Format("/resources{0}", filename));

      String area = filename.Remove(filename.LastIndexOf('/') + 1);
      FileNames.Add(string.Format("/resources{0}", area + "area.dat"));
      FileNames.Add(string.Format("/resources{0}", area + "mapnotes.not"));

      //Instances
      br.BaseStream.Position = instanceOffset;
      UInt32 numInstances = br.ReadUInt32();

      for (UInt32 i = 0; i < numInstances; i++) {
        UInt32 instanceHeader = br.ReadUInt32();

        if (instanceHeader != 0xABCD1234) { // 0x3412CDAB
          throw new Exception();
        } else {
          // string sdifn = "";
        }

        br.ReadByte();
        br.ReadUInt64();
        br.ReadUInt64();
        br.ReadByte();

        UInt32 numProperties = br.ReadUInt32();
        UInt32 propteriesLength = br.ReadUInt32();

        Int64 startOffset = br.BaseStream.Position;

        br.ReadByte();

        try {
          for (UInt32 p = 0; p < numProperties; p++) {
            DatTypeId type = (DatTypeId)br.ReadByte();
            UInt32 propertyId = br.ReadUInt32();

            if (!_properties.ContainsKey(propertyId)) {
              _properties.Add(propertyId, type);
            } else if (_properties[propertyId] != type) {
              //throw new IndexOutOfRangeException();
              DatTypeId oldtype = _properties[propertyId];
            }

            Object o = null;

            switch (type) {
              case DatTypeId.Boolean:
                Byte by = br.ReadByte();

                if (by > 1) throw new IndexOutOfRangeException();

                Boolean bo = Convert.ToBoolean(by);
                o = bo;
                break;

              case DatTypeId.Unknown1:
                Int32 it = br.ReadInt32();
                if (it > 1) o = it;
                break;
              //case DatTypeId.Unknown2:
              //    break;

              case DatTypeId.UInt32:
                UInt32 uit = br.ReadUInt32();
                o = uit;
                break;

              case DatTypeId.Single:
                Single f = br.ReadSingle();
                o = f;
                break;

              case DatTypeId.UInt64:
                UInt64 l = br.ReadUInt64();
                o = l;
                break;

              case DatTypeId.Vector3:
                List<Single> vec3 = new List<Single> {
                  br.ReadSingle(),
                  br.ReadSingle(),
                  br.ReadSingle()
                };
                o = vec3;
                break;

              case DatTypeId.Unknown7:
                //byte[] bytes = br.ReadBytes(16);
                List<Single> vec4 = new List<Single> {
                  br.ReadSingle(),
                  br.ReadSingle(),
                  br.ReadSingle(),
                  br.ReadSingle()
                };
                o = vec4;
                break;

              case DatTypeId.String:
                UInt32 strlen = br.ReadUInt32();
                StringBuilder str = new StringBuilder((Int32)strlen);

                Char c1 = br.ReadChar();
                Char c2 = br.ReadChar();

                UInt32 charsRead = 1;

                while (c1 != '\0' && c1 != '\0' && charsRead < strlen) {
                  str.Append(c1);

                  if (c2 != '\0') throw new IndexOutOfRangeException();

                  c1 = br.ReadChar();
                  c2 = br.ReadChar();

                  charsRead++;
                }

                o = str.ToString();

                if (!string.IsNullOrWhiteSpace((String)o)) {
                  switch (propertyId) {
                    case 3261558584:    // FxSpecName
                      fxspecs.Add((String)o);
                      break;

                    case 2393024011:    // spnAnimation or spnNpcIdleAnimationName
                      _animFileNames.Add(((String)o).ToLower());
                      break;

                    case 964697786:     // Tag
                      FileNames.Add("/resources" + area + (String)o + ".dat");
                      textures.Add(area + (String)o);
                      break;

                    //Skip Start
                    case 2957064701:    // PortalTarget
                    case 4255290973:    // rgnVolumeData
                    case 669968511:     // rgnCharacteristics
                    case 3166688232:    // ParentMapTag
                    case 1768825245:    // tesselation 
                    case 240466284:     // resolution
                    case 3106719576:    // StopEvent 
                    case 948461446:     // PlayEvent
                    case 466906898:     // rgnRespawnMedCenter
                    case 3160985587:    // Intensity
                    case 384379389:     // Range
                    case 3430452781:    // FxRespawnDelay
                    case 3629101973:    // TriggerParam
                    case 273365031:     // Speed
                    case 2335395941:    // Path
                    case 713588192:     // spnTagFromEncounter                                        
                    case 1467655203:    // wtrVertexData
                    case 113668568:     // DepthTexture
                    case 3060549674:    // spnPhaseInstanceName
                    case 773762347:     // name
                    case 3235228203:    // FxMaxSpawnDistance
                    case 446782081:     // DiffuseColor
                    case 2793072227:    // Color
                    case 3179516067:    // TriggerScript
                    case 3424594045:    // JointFlags
                    case 3084732969:    // Deformation_X
                    case 3084732970:    // Deformation_Y
                    case 4158591558:    // Divisions
                    case 3839584892:    // LightningWidth
                    case 2857627687:    // DeltaRotation3D                                        
                    case 3522231145:    // LeafTinting
                    case 4012120889:    // GlossColor
                    case 489737334:     // LODFactor
                    case 3402983087:    // BoneName
                    case 4268140818:    // some type of color
                    case 3069428699:    // regionEdgeData
                    case 2766070679:    // DeepColor
                    case 3671420588:    // FogColor1
                    case 3671420589:    // FogColor2
                    case 1620832956:    // FogColorSky
                      break;
                    //Skip End

                    case 999479220:     // Falloff
                    case 1820631501:    // IlluminationMap
                    case 1117554570:    // RampMap
                    case 1412492047:    // SurfaceMap 
                    case 2545768381:    // NormalMap2
                    case 2545768380:    // NormalMap1
                    case 2829380834:    // gfxMovieName
                    case 3003166540:    // ProjectionTexture                                            
                    default:
                      textures.Add((String)o);
                      break;
                  }
                }
                break;

              case DatTypeId.Data:
                UInt32 datalen = br.ReadUInt32();
                br.BaseStream.Position += datalen;
                break;

              default:
                Int64 curpos = br.BaseStream.Position; //this is for debugging new formats found
                Byte[] bities = br.ReadBytes(32);

                br.BaseStream.Position = curpos;

                throw new IndexOutOfRangeException();
            }
            //break;
          }

        }
        catch (Exception) {
          br.BaseStream.Position = startOffset + propteriesLength;
        }
      }

      foreach (String fxs in fxspecs) {
        FileNames.Add(
          String.Format(
            "/resources/art/fx/fxspec/{0}.fxspec",
            fxs.ToLower()
          ).Replace("\\", "/").Replace("//", "/").Replace(".fxspec.fxspec", ".fxspec")
        );
      }

      foreach (String tex in textures) {
        String file =
          ("/resources/" + tex.ToLower()).Replace("\\", "/").Replace("//", "/").Replace(".dds", "");
        FileNames.Add(string.Format("{0}.dds", file));
        FileNames.Add(string.Format("{0}.tiny.dds", file));
        FileNames.Add(string.Format("{0}.tex", file));
      }
    }
    #endregion
    #region Old Format Readers
    internal static void ParseAreaDAT(List<String> lines) {
      if (lines == null) throw new ArgumentNullException(nameof(lines));

      return;
    }
    internal static void ParseRoomDAT(List<String> lines) {
      if (lines == null) throw new ArgumentNullException(nameof(lines));

      return;
    }
    internal void ParseCharacterDAT(List<String> lines) {
      List<String> sectionNames = new List<String>(new String[] { "[PARTS]" });

      lines.RemoveAt(0);

      String skeleton_name =
        lines[0].Split(new String[] { "for " }, StringSplitOptions.None).Last().Trim();
      FileNames.Add("/resources/art/dynamic/spec/" + skeleton_name + ".gr2");
      Dictionary<String, String> parts = new Dictionary<String, String>();
      String current = "";

      foreach (String line in lines) {
        if (sectionNames.Contains(line)) current = line;
        else {
          if (line.Contains(':') || line.Contains('#')) continue;

          switch (current) {
            case "[PARTS]":
              if (line == "") continue;

              String[] split = line.Split('=');

              if (!parts.ContainsKey(split[0])) parts.Add(split[0], split[1]);

              break;

            default:
              break;
          }
        }
      }

      if (parts.ContainsKey("Model")) {
        FileNames.Add("/resources/art/dynamic/spec/" + parts["Model"]);
        FileNames.Add("/resources/art/dynamic/spec/" + parts["Model"].Replace(".dyc", ".dat"));
        FileNames.Add("/resources/art/dynamic/spec/" + parts["Model"].Replace(".dyc", ".mag"));
      }

      if (parts.ContainsKey("AnimMetadataFqn")) {
        String[] temp = parts["AnimMetadataFqn"].Split(',');
        TorArchive.Assets assets = TorArchive.AssetHandler.Instance.GetCurrentAssets();

        foreach (String item in temp) {
          String tempName = "/resources/" + item.Replace('\\', '/').Replace("//", "/");
          FileNames.Add(tempName);

          if (parts.ContainsKey("AnimNetworkFolder")) {
            String netfold =
              String.Format(
                "/resources/{0}",
                parts["AnimNetworkFolder"].Replace('\\', '/').Replace("//", "/")
              );
            TorArchive.File file = assets.FindFile(tempName);

            if (file != null) {
              try {
                using Stream fileStream = file.OpenCopyInMemory();
                XDocument doc = XDocument.Load(fileStream);
                XElement aamElement = doc.Element("aam");

                if (aamElement != null) {
                  XElement actionElement = aamElement.Element("actions");

                  if (actionElement != null) {
                    IEnumerable<XElement> actionList = actionElement.Elements("action");

                    foreach (XElement action in actionList) {
                      String actionName = action.Attribute("name").Value;

                      if (action.Attribute("actionProvider") != null) {
                        String actionProvider = action.Attribute("actionProvider").Value + ".mph";
                        _animFileNames.Add(netfold + actionProvider);
                        _animFileNames.Add(netfold + actionProvider + ".amx");
                      }

                      if (action.Attribute("animName") != null) {
                        String animationName = action.Attribute("animName").Value;

                        if (actionName != animationName) {
                          animationName += ".jba";
                          _animFileNames.Add(netfold + animationName);
                        }
                      }

                      actionName += ".jba";
                      _animFileNames.Add(netfold + actionName);
                    }
                  }

                  XElement networkElem = aamElement.Element("networks");
                  if (networkElem != null) {
                    IEnumerable<XElement> networkList = networkElem.Descendants("literal");

                    foreach (XElement network in networkList) {
                      String fqnName = network.Attribute("fqn").Value;

                      if (fqnName != null) {
                        _animFileNames.Add(netfold + fqnName);
                        _animFileNames.Add(netfold + fqnName + ".amx");
                      }
                    }
                  }

                  XElement inputElement = aamElement.Element("inputs");

                  if (inputElement != null) {
                    IEnumerable<XElement> inputList =
                      inputElement.Elements("input").Descendants("value");

                    foreach (XElement input in inputList) {
                      String fqnName = input.Attribute("name").Value;

                      if (fqnName != null) {
                        _animFileNames.Add(netfold + fqnName);
                        _animFileNames.Add(netfold + fqnName + ".amx");
                        _animFileNames.Add(netfold + fqnName + ".jba");
                      }
                    }
                  }
                }
              }
              catch (Exception ex) {
                _errors.Add("File: " + tempName);
                _errors.Add(ex.Message + ":");
                _errors.Add(ex.StackTrace);
                _errors.Add("");
              }
            }
          }
        }
      }

      if (parts.ContainsKey("AnimLibraryFqn")) {
        String tempName = "/resources/" + parts["AnimLibraryFqn"];
        FileNames.Add(tempName.Replace('\\', '/').Replace("//", "/"));
      }

      if (parts.ContainsKey("AnimShareMetadataFqn")) {
        String tempName = "/resources/" + parts["AnimShareMetadataFqn"];
        FileNames.Add(tempName.Replace('\\', '/').Replace("//", "/"));
      }

      /* Disabled - Enable to find new keys that have slashes
      HashSet<string> animKeys = new HashSet<String>(new String[] {
        "AnimShareMetadataFqn", 
        "AnimLibraryFqn", 
        "AnimMetadataFqn", 
        "Model", 
        "AnimNetworkFolder" 
        }
      );
      
      foreach (var part in parts) {
        if (animKeys.Contains(part.Key)) continue;
        
        if (part.Value.Contains('\\')) {
          Debug.WriteLine(part.Key.ToString());
        }

        if (part.Value.Contains('/')) {
          Debug.WriteLine(part.Key.ToString());
        }              
      }
      */
    }
    #endregion
    internal void WriteFile() {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      if (FileNames.Count > 0) {
        StreamWriter outputNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_file_names.txt", false);

        foreach (String file in FileNames) {
          outputNames.WriteLine(file.Replace("\\", "/"));
        }

        outputNames.Close();
        FileNames.Clear();
      }

      if (_animFileNames.Count > 0) {
        StreamWriter outputAnimNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_anim_file_names.txt", false);

        foreach (String file in _animFileNames) {
          outputAnimNames.WriteLine(file.Replace("\\", "/"));
        }

        outputAnimNames.Close();
        _animFileNames.Clear();
      }

      if (_errors.Count > 0) {
        StreamWriter outputErrors =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_error_list.txt", false);

        foreach (String error in _errors) {
          outputErrors.WriteLine(error);
        }

        outputErrors.Close();
        _errors.Clear();
      }
    }
  }
  internal enum DatTypeId : Byte {
    Boolean  = 0x00,
    Unknown1 = 0x01,
    Unknown2 = 0x02,
    UInt32   = 0x03, // may be Int32
    Single   = 0x04,
    UInt64   = 0x05,
    Vector3  = 0x06,
    Unknown7 = 0x07,
    String   = 0x08,
    Data     = 0x09
  }
}
