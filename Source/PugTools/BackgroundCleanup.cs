using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PugTools {
  /// <summary>
  /// Serializes expensive browser shutdown work on one lowest-priority background thread.
  /// Releasing thousands of D3D/COM/tree resources on multiple ThreadPool threads at once can
  /// otherwise make closing a few large browsers noticeably stall the entire machine.
  /// </summary>
  internal static class BackgroundCleanup {
    private static readonly BlockingCollection<Action> QueueItems = new BlockingCollection<Action>();
    private static readonly Thread Worker = CreateWorker();

    private static Thread CreateWorker() {
      Thread thread = new Thread(Run) {
        IsBackground = true,
        Name = "PugTools browser cleanup",
        Priority = ThreadPriority.Lowest
      };
      thread.Start();
      return thread;
    }

    internal static void Enqueue(Action action) {
      if (action == null) return;
      try { QueueItems.Add(action); } catch { }
    }

    private static void Run() {
      foreach (Action action in QueueItems.GetConsumingEnumerable()) {
        try { action(); } catch { }
        // Yield briefly between very large D3D/COM cleanup batches so the renderer/UI remains responsive.
        Thread.Sleep(4);
      }
    }
  }
}
