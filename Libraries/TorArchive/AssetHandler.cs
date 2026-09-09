using System;

namespace TorArchive {
  public class AssetHandler : IDisposable {

    #region Fields
    private static readonly AssetHandler s_instance = new AssetHandler();
    private readonly Object m_currentLock = new Object();
    private readonly Object m_previousLock = new Object();
    private Assets m_currentData;
    private Assets m_previousData;
    private volatile Boolean m_currentLoaded;
    private volatile Boolean m_previousLoaded;

    // public Dictionary<string, Assets> loadedData = new Dictionary<string, Assets>();

    #endregion Fields

    #region IDisposable
    private Boolean m_disposed = false;

    ~AssetHandler() {
      Dispose(false);
    }

    public void Dispose() {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(Boolean disposing) {
      if (m_disposed) {
        return;
      }

      if (disposing) {
        m_currentData?.Dispose();
        m_previousData?.Dispose();
      }
      m_disposed = true;
    }

    #endregion IDisposable

    #region Methods
    public Assets GetCurrentAssets(String path = null, Boolean isPTR = false) {
      // Browser startup is not a hot path; always take the same lock used by unload so a source
      // cannot be disposed between a fast-path Loaded check and the returned reference.
      lock (m_currentLock) {
        if (m_currentLoaded) return m_currentData;

        if (path == null)
          throw new ArgumentException("No path to the assests was provided");

        m_currentData = new Assets(path);
        m_currentData.Load(isPTR);
        m_currentLoaded = true;
        return m_currentData;
      }
    }

    public Assets GetPreviousAssets(String path = null, Boolean isPTR = false) {
      lock (m_previousLock) {
        if (m_previousLoaded) return m_previousData;

        if (path == null)
          throw new ArgumentException("No path to the assests were provided");

        m_previousData = new Assets(path);
        m_previousData.Load(isPTR);
        m_previousLoaded = true;
        // HashDictionaryInstance is shared by every open browser. Loading a comparison build
        // must not tear down the filename cache used by the current build/browser windows.
        return m_previousData;
      }
    }

    #endregion Methods

    #region Properties
    public static AssetHandler Instance => s_instance;
    public Boolean CurrentLoaded => m_currentLoaded;
    public Boolean PreviousLoaded => m_previousLoaded;

    #endregion Properties

    #region Unload Data
    public void UnloadAllAssets() {
      // Use the same locks as GetCurrent/GetPrevious so an explicit unload cannot dispose an
      // Assets instance while another browser is in the middle of first-load publication.
      lock (m_currentLock) {
        if (m_currentLoaded) {
          m_currentData?.Dispose();
          m_currentData = null;
          m_currentLoaded = false;
        }
      }
      lock (m_previousLock) {
        if (m_previousLoaded) {
          m_previousData?.Dispose();
          m_previousData = null;
          m_previousLoaded = false;
        }
      }
      // Assets can be very large. Let the runtime reclaim them incrementally instead of forcing
      // a full stop-the-world collection on the UI path.
    }
    public void UnloadCurrentAssets() {
      lock (m_currentLock) {
        if (m_currentLoaded) {
          m_currentData?.Dispose();
          m_currentData = null;
          m_currentLoaded = false;
        }
      }
    }
    public void UnloadPreviousAssets() {
      lock (m_previousLock) {
        if (m_previousLoaded) {
          m_previousData?.Dispose();
          m_previousData = null;
          m_previousLoaded = false;
        }
      }
    }

    #endregion Unload Data

  }
}
