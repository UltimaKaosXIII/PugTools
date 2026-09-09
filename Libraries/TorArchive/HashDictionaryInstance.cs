using System;

using nsHashDictionary;

namespace TorArchive {
  public class HashDictionaryInstance {

    #region Constructors
    public HashDictionaryInstance() {
      Dictionary = new HashDictionary();
      Dictionary.LoadBinaryHashList();
      Loaded = true;
    }

    #endregion Constructors

    #region Fields
    // Keep the very large filename dictionary genuinely lazy. In particular, asking whether
    // there is anything to save while the main window is closing must not instantiate the
    // singleton and read the complete hash list just for that check.
    private static readonly Lazy<HashDictionaryInstance> s_instance =
      new Lazy<HashDictionaryInstance>(() => new HashDictionaryInstance());
    private readonly Object m_sync = new Object();
    private volatile Boolean m_loaded;

    #endregion Fields

    #region Methods
    public void Load() {
      lock (m_sync) {
        if (Loaded) return;
        Dictionary.LoadBinaryHashList();
        Loaded = true;
      }
    }

    public void Unload() {
      lock (m_sync) {
        Dictionary = new HashDictionary();
        Loaded = false;
      }
      // Do not force a full, stop-the-world collection here. The dictionary is process-wide and
      // can contain millions of rows; an explicit GC made browser/tool transitions look frozen.
    }

    #endregion Methods

    #region Properties
    public HashDictionary Dictionary { get; private set; }
    public Boolean Loaded {
      get => m_loaded;
      private set => m_loaded = value;
    }
    public static Boolean IsCreated => s_instance.IsValueCreated;
    public static HashDictionaryInstance Instance => s_instance.Value;

    #endregion Properties
  }
}
