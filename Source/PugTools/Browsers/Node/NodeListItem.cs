using System;
using System.Collections.Generic;
using System.Diagnostics;
using GomLib;
using GomList = GomLib.GomTypes.List;
using Map = GomLib.GomTypes.Map;

namespace PugTools {
  public class NodeListItem { // Must be public for interop with ObjectListView
    internal List<NodeListItem> children = new List<NodeListItem>();
    internal Object value;
    // Optional browser cross-link metadata. This is intentionally kept separate from value so
    // the existing raw GOM display/serialization remains byte-for-byte unchanged.
    internal Object NavigationTarget { get; set; }

    public String DisplayName { get; } // Must be public for interop with ObjectListView
    public String DisplayValue { get; private set; } // Must be public for interop with ObjectListView
    public Object Name { get; } // Must be public for interop with ObjectListView
    public String Type { get; private set; } // Must be public for interop with ObjectListView
    public String FieldId { get; internal set; } = String.Empty; // Optional raw DOM field id for reader-style inspection.

    // Sort of hacky to avoid changing all the existing stuff.
    private NodeListItem(Object name, Object value, GomType type = null) {
      Name = name;

      if (name is UInt64 potNodeID) {
        DataObjectModel currentDom = DomHandler.Instance.GetCurrentDOM();

        if (currentDom.DomTypeMap.TryGetValue(potNodeID, out DomType nodeLookup)) {
          DisplayName = potNodeID.ToString() + "  (" + nodeLookup.Name.ToString() + ")";
        } else {
          DisplayName = name.ToString();
        }

        NodeListItemSetup(name.ToString(), value, type);
      } else {
        DisplayName = name.ToString();
        NodeListItemSetup(name.ToString(), value, type);
      }
    }
    internal NodeListItem(String name, Object value, GomType type = null) {
      Name = name;
      DisplayName = name;
      NodeListItemSetup(name, value, type);
    }
    private void NodeListItemSetup(String name, Object value, GomType type = null) {
      try {
        this.value = value;
        // Debug.WriteLine(name);
      }
      catch (Exception) { }

      if (type != null) {
        try {
          if (type.TypeId == GomTypeId.Map) {
            Type = type.ToString();
            Map map = (Map)type;
            GomType keyType = map.KeyType;
            GomType valueType = map.ValueType;
            Dictionary<Object, Object> objDict = (Dictionary<Object, Object>)this.value;

            foreach (KeyValuePair<Object, Object> item in objDict) {
              NodeListItem child = new NodeListItem(item.Key, item.Value, valueType);

              if (DisplayName == "locTextRetrieverMap") {
                GomObjectData currentValue = (GomObjectData)item.Value;
                Int64 stringId =
                  currentValue.ValueOrDefault<Int64>("strLocalizedTextRetrieverStringID", 0);
                String stringBucket =
                  currentValue.ValueOrDefault<String>("strLocalizedTextRetrieverBucket", null);

                if (stringId != 0 && stringBucket != null) {
                  DataObjectModel currentDom = DomHandler.Instance.GetCurrentDOM();
                  String currentStringValue =
                    currentDom.StringTable.TryGetString(stringBucket, stringId);
                  NodeListItem stringChild = new NodeListItem("String Value", currentStringValue);

                  child.children.Add(stringChild);
                }
              }

              children.Add(child);
            }

            DisplayValue = "";
          } else if (type.TypeId == GomTypeId.List) {
            Type = type.ToString();
            GomList listType = (GomList)type;
            GomType valueType = listType.ContainedType;
            List<Object> list = (List<Object>)this.value;
            Int32 count = 0;

            foreach (Object item in list) {
              NodeListItem child = new NodeListItem(count.ToString(), item, valueType);

              children.Add(child);
              count++;
            }

            DisplayValue = "";
          } else if (type.TypeId == GomTypeId.EmbeddedClass) {
            Type = type.ToString();
            GomObjectData obj = (GomObjectData)this.value;

            foreach (KeyValuePair<String, Object> objItem in obj.Dictionary) {
              if (objItem.Key.Contains("Script_")) continue;

              DomClass classLookup = (DomClass)obj.Dictionary["Script_Type"];
              DomField fieldLookup = classLookup.Fields.Find(x => x.Name == objItem.Key.ToString());

              if (fieldLookup == null) {
                try {
                  if (UInt64.TryParse(objItem.Key, out UInt64 id))
                    fieldLookup = classLookup.Fields.Find(x => x.Id == id);
                }
                catch (Exception) { }
              }

              if (fieldLookup != null) {
                NodeListItem child =
                  new NodeListItem(objItem.Key.ToString(), objItem.Value, fieldLookup.GomType) {
                    FieldId = fieldLookup.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NavigationTarget = fieldLookup
                  };
                children.Add(child);
              } else {
                NodeListItem child =
                  new NodeListItem(objItem.Key.ToString(), objItem.Value, null);
                children.Add(child);
              }
            }
          } else if (type.TypeId == GomTypeId.Vec3) {
            List<Single> list = (List<Single>)this.value;
            DisplayValue = "(" + string.Join(", ", list.ToArray()) + ")";

            Type = type.ToString();
          } else if (type.TypeId == GomTypeId.UInt64) {
            DataObjectModel currentDom = DomHandler.Instance.GetCurrentDOM();
            currentDom.DomTypeMap.TryGetValue((UInt64)this.value, out DomType nodeLookup);

            if (nodeLookup != null) {
              DisplayValue = value.ToString() + "  (" + nodeLookup.Name.ToString() + ")";
            } else {
              DisplayValue = value.ToString();
            }

            Type = type.ToString();
          } else {
            Type = type.ToString();

            if (this.value != null && DisplayValue == null) DisplayValue = value.ToString();
          }
        }
        catch (Exception ex) {
          Debug.WriteLine("caught exception");
          Debug.WriteLine("exception pause here" + ex.ToString());
        }
      } else {
        Object value1 = this.value;

        if (this.value is Dictionary<Object, Object> objDict) {
          foreach (KeyValuePair<Object, Object> item in objDict) {
            NodeListItem child = new NodeListItem(item.Key.ToString(), item.Value);

            if (DisplayName == "locTextRetrieverMap") {
              GomObjectData currentValue = (GomObjectData)item.Value;
              Int64 stringId =
                currentValue.ValueOrDefault<Int64>("strLocalizedTextRetrieverStringID", 0);
              String stringBucket =
                currentValue.ValueOrDefault<String>("strLocalizedTextRetrieverBucket", null);

              if (stringId != 0 && stringBucket != null) {
                DataObjectModel currentDom = DomHandler.Instance.GetCurrentDOM();
                String currentStringValue =
                  currentDom.StringTable.TryGetString(stringBucket, stringId);
                NodeListItem stringChild = new NodeListItem("String Value", currentStringValue);

                child.children.Add(stringChild);
              }
            }

            child.Type = GetType(item.Key.ToString());
            children.Add(child);
          }

          DisplayValue = "";
        } else if (this.value is Dictionary<String, String> stringDict) {
          foreach (KeyValuePair<String, String> item in stringDict) {
            NodeListItem child = new NodeListItem(item.Key.ToString(), item.Value) {
              Type = GetType(item.Key.ToString())
            };

            children.Add(child);
          }

          DisplayValue = "";
        } else if (this.value is GomObjectData obj) {
          foreach (KeyValuePair<String, Object> objItem in obj.Dictionary) {
            NodeListItem child = new NodeListItem(objItem.Key.ToString(), objItem.Value) {
              Type = GetType(objItem.Key.ToString())
            };

            children.Add(child);
          }
        } else if (this.value is List<Object>) {
          List<Object> list = (List<Object>)this.value;

          foreach (Object item in list) {
            NodeListItem child = new NodeListItem("", item) {
              Type = GetType(item.ToString())
            };

            children.Add(child);
          }

          DisplayValue = "";
        } else if (value1 is List<String>) {
          List<String> list = (List<String>)this.value;
          foreach (String item in list) {
            NodeListItem child = new NodeListItem("", item) {
              Type = GetType(item.ToString())
            };

            children.Add(child);
          }

          if (children.Count == 0) {
            if (Name != null && DisplayValue == "") DisplayValue = name;
          } else
            DisplayValue = "";
        } else if (this.value is DEP_Entry entry) {
          if (entry.Dependencies.Count > 0) {
            foreach (String dependency in entry.Dependencies) {
              NodeListItem child = new NodeListItem("", dependency);

              children.Add(child);
            }
          }
        } else {
          if (this.value != null) {
            if (DisplayValue == null) DisplayValue = value.ToString();
          }
        }
      }
    }
    internal static void ResetTreeListViewColumns(BrightIdeasSoftware.TreeListView tlv) {
      BrightIdeasSoftware.OLVColumn olvColumn1 = new BrightIdeasSoftware.OLVColumn();
      BrightIdeasSoftware.OLVColumn olvColumn2 = new BrightIdeasSoftware.OLVColumn();

      olvColumn1.AspectName = nameof(Name);
      olvColumn1.CellPadding = null;
      olvColumn1.Text = "Name";

      olvColumn2.AspectName = nameof(DisplayValue);
      olvColumn2.FillsFreeSpace = true;
      olvColumn2.CellPadding = null;
      olvColumn2.MinimumWidth = 40;
      olvColumn2.Text = "Value";
      olvColumn2.WordWrap = true;

      tlv.Columns.Clear();
      tlv.Columns.Add(olvColumn1);
      tlv.Columns.Add(olvColumn2);
    }
    private static String GetType(String item) {
      DataObjectModel currentDom = DomHandler.Instance.GetCurrentDOM();
      currentDom.NodeLookup[typeof(DomField)].TryGetValue(item.ToString(), out DomType fieldLookup);
      String type;

      if (fieldLookup != null) {
        DomField fieldType = (DomField)fieldLookup;
        type = fieldType.GomType.ToString();
      } else {
        type = "Unknown";
      }

      return type;
    }
  }
}
