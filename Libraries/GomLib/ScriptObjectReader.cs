using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GomLib {
  public class ScriptObjectReader {
    [Newtonsoft.Json.JsonIgnore]
    DataObjectModel _dom;

    public ScriptObjectReader(DataObjectModel dom) {
      _dom = dom;
    }

    public void Flush() {
      _dom = null;
    }

    public GomObjectData ReadObject(DomClass domClass, GomBinaryReader reader, DataObjectModel dom) {
      if (_dom == null) _dom = dom;
      GomObjectData result = new GomObjectData();
      IDictionary<string, object> resultDict = result.Dictionary;
      if (domClass != null) {
        resultDict.Add("Script_Type", domClass);
      }

      // NODE/ClassView payloads contain two varints: total fields and stored fields.
      // Historic PugTools called the first one Script_TypeId; retain that key for consumers,
      // but parse both values using SWTOR's signed varint format just like Jedipedia.
      long totalFieldsRaw = reader.ReadSignedNumber();
      long storedFieldsRaw = reader.ReadSignedNumber();
      if (totalFieldsRaw < 0 || storedFieldsRaw < 0 || storedFieldsRaw > totalFieldsRaw || storedFieldsRaw > Int32.MaxValue)
        throw new InvalidOperationException($"Invalid object field counts {totalFieldsRaw}/{storedFieldsRaw}.");

      resultDict.Add("Script_TypeId", unchecked((ulong)totalFieldsRaw));
      int numFields = checked((int)storedFieldsRaw);
      resultDict.Add("Script_NumFields", numFields);

      ulong fieldId = 0;
      for (var i = 0; i < numFields; i++) {
        // Field ids are DELTAS encoded as signed varints.  Negative deltas are common in old
        // nodes; adding them as unsigned numbers shifts the stream/schema association and is
        // one of the causes of apparently random "Unknown GomType 114" failures.
        long delta = reader.ReadSignedNumber();
        fieldId = unchecked(fieldId + (ulong)delta);

        DomField field = _dom.Get<DomField>(fieldId);

        // The serialized node always carries its actual data type byte.  Consume it even for
        // known fields.  When it agrees with client.gom, use the declared instance because it
        // contains Enum/Class/List/Map reference metadata.  When it differs, trust the bytes in
        // the node so a beta field whose live schema changed cannot desynchronise all following
        // fields.
        GomType inlineType = _dom.GomTypeLoader.Load(reader, _dom, false);
        GomType fieldType = inlineType;
        if (field != null && field.GomType != null) {
          if (inlineType.TypeId == field.GomType.TypeId)
            fieldType = field.GomType;
          else
            Debug.WriteLine("Serialized field type " + inlineType.TypeId + " differs from DOM type " + field.GomType.TypeId + " for " + field.Name + ". Using serialized type.");
        }

        object fieldValue = fieldType.ReadData(_dom, reader);
        string domClassNullCheck = ((object)domClass ?? "Unknown").ToString();

        string fieldName;
        if ((field != null) && (!string.IsNullOrEmpty(field.Name))) {
          fieldName = field.Name;
          if (!_dom.NamedMap.ContainsKey(domClassNullCheck)) {
            _dom.NamedMap.Add(domClassNullCheck, new HashSet<string>());
          }
          if (domClass != null) {
            _dom.NamedMap[domClassNullCheck].Add(fieldName);
          }
        } else {
          fieldName = _dom.GetStoredTypeName(fieldId);
          if (fieldName == null) {
            if (!_dom.UnnamedMap.ContainsKey(domClassNullCheck))
              _dom.UnnamedMap.Add(domClassNullCheck, new HashSet<ulong>());
            if (domClass != null)
              _dom.UnnamedMap[domClassNullCheck].Add(fieldId);
            fieldName = fieldId.ToString();
          }
        }

        // Corrupt/duplicate schemas should not crash extraction merely because two fields resolve
        // to the same display name. Preserve the first friendly name and expose later duplicates
        // under their numeric id.
        if (resultDict.ContainsKey(fieldName)) fieldName = fieldId.ToString();
        resultDict[fieldName] = fieldValue;
      }

      return result;
    }
  }
}
