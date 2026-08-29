using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileFormats;
using ArchiveFile = TorArchive.File;

namespace PugTools {
  // Keeps archive I/O and Granny parsing off the D3D/render thread.  GPU creation deliberately remains in
  // View_AREA: D3D11 immediate contexts are thread-affine in this viewer.
  internal sealed class WorldModelStreamRequest {
    public ulong AssetId { get; }
    public string AssetPath { get; }
    public bool IsSpeedTree { get; }

    public WorldModelStreamRequest(ulong assetId, string assetPath, bool isSpeedTree) {
      AssetId=assetId; AssetPath=assetPath; IsSpeedTree=isSpeedTree;
    }
  }

  internal sealed class WorldModelStreamResult {
    public WorldModelStreamRequest Request { get; }
    public GR2 Model { get; }
    public Exception Error { get; }
    public WorldModelStreamResult(WorldModelStreamRequest request, GR2 model, Exception error) { Request=request; Model=model; Error=error; }
  }

  internal sealed class WorldModelStreamer : IDisposable {
    private readonly Area area;
    private readonly ConcurrentQueue<WorldModelStreamRequest> highPriority=new ConcurrentQueue<WorldModelStreamRequest>();
    private readonly ConcurrentQueue<WorldModelStreamRequest> normalPriority=new ConcurrentQueue<WorldModelStreamRequest>();
    private readonly ConcurrentQueue<WorldModelStreamResult> completed=new ConcurrentQueue<WorldModelStreamResult>();
    // 0=normal queued, 1=high queued, 2=worker owns request, 3=result queued for the render thread,
    // 5=result consumed/installed. A high-priority request may promote a still queued neighbour; stale queue
    // entries are ignored by the worker rather than decoded twice. Keeping 3 and 5 separate is important for
    // the first-frame gate: a background parse is not display-ready merely because it has reached `completed`.
    private readonly ConcurrentDictionary<ulong,byte> requested=new ConcurrentDictionary<ulong,byte>();
    // A missing/corrupt world model must not be retried every frame. Apart from wasting a worker this used to keep
    // the initial loading screen open forever because the same request could never reach a successful model state.
    private readonly ConcurrentDictionary<ulong,byte> failed=new ConcurrentDictionary<ulong,byte>();
    private readonly CancellationTokenSource cancellation=new CancellationTokenSource();
    private readonly Task[] workers;
    // This is deliberately a bound on work-in-flight, not on the set of models that may be
    // visible.  24 was too small for large connected interiors: a hangar can need more than
    // 24 unique shell pieces before it has a usable floor.  CPU/VRAM residency is bounded
    // separately by View_AREA, where an object is only uploaded once it is actually drawn. A 192-item burst
    // still let obsolete camera work occupy minutes of GR2 parsing before queue cancellation existed. The current
    // demand set now actively cancels stale queued work, so a somewhat wider window is safe and prevents large
    // Corellia rooms from repeatedly stalling admission at doorways while decoder workers sit on a short queue.
    private const int MaxOutstandingRequests=320;
    private int outstandingRequests;

    public WorldModelStreamer(Area area, int workerCount) {
      this.area=area ?? throw new ArgumentNullException(nameof(area));
      workerCount=Math.Max(1,Math.Min(6,workerCount)); workers=new Task[workerCount];
      for(int i=0;i<workers.Length;i++) workers[i]=Task.Run(WorkerLoop);
    }

    public bool Request(WorldModelStreamRequest request, bool high) {
      if(request==null)return false;
      if(failed.ContainsKey(request.AssetId))return false;
      byte priority=high?(byte)1:(byte)0;
      if(requested.TryAdd(request.AssetId,priority)) {
        // Completed parse results count until View_AREA consumes them. Otherwise fast workers can fill RAM with
        // hundreds of decoded GR2s while the render thread deliberately accepts only a few per frame.
        if(Interlocked.Increment(ref outstandingRequests)>MaxOutstandingRequests){Interlocked.Decrement(ref outstandingRequests);requested.TryRemove(request.AssetId,out _);return false;}
        if(high)highPriority.Enqueue(request);else normalPriority.Enqueue(request);return true;
      }
      // Camera movement frequently promotes a previously speculative neighbour. Without this, it could remain
      // behind an arbitrary normal-priority queue and present as a missing room at a doorway.
      if(high&&requested.TryUpdate(request.AssetId,1,0)){highPriority.Enqueue(request);return true;}
      return false;
    }

    public bool TryDequeue(out WorldModelStreamResult result) => completed.TryDequeue(out result);
    public void MarkResultConsumed(ulong assetId) {
      if(requested.TryGetValue(assetId,out byte state)&&state==3)requested.TryUpdate(assetId,5,3);
      if(Interlocked.CompareExchange(ref outstandingRequests,0,0)>0)Interlocked.Decrement(ref outstandingRequests);
    }
    public bool IsKnown(ulong assetId) => requested.ContainsKey(assetId)||failed.ContainsKey(assetId);
    // Promotion decisions must distinguish a speculative request still waiting in the normal queue from models that
    // are already high-priority, decoding, completed, or installed. Treating every "known" model as promotable made
    // RequestRoomModels repeatedly select the same already-settled nearest assets and left the rest of a newly current
    // room stranded in the low band.
    public bool IsNormalQueued(ulong assetId) => requested.TryGetValue(assetId,out byte state)&&state==0;
    public bool IsFailed(ulong assetId) => failed.ContainsKey(assetId);
    public bool IsSettled(ulong assetId) => failed.ContainsKey(assetId)||(requested.TryGetValue(assetId,out byte state)&&state==5);
    public int OutstandingCount => Math.Max(0,Interlocked.CompareExchange(ref outstandingRequests,0,0));
    public int CompletedCount => completed.Count;
    // ConcurrentQueue has no removal operation. Mark a no-longer-demanded queued item as cancelled, then let the
    // worker discard its stale queue node. Worker-owned/completed work is deliberately left alone.
    public int CancelQueuedExcept(ISet<ulong> keep) {
      int cancelled=0;
      foreach(KeyValuePair<ulong,byte> pair in requested.ToArray()) {
        if(keep!=null&&keep.Contains(pair.Key)||(pair.Value!=0&&pair.Value!=1))continue;
        if(!requested.TryUpdate(pair.Key,4,pair.Value))continue;
        requested.TryRemove(pair.Key,out _);Interlocked.Decrement(ref outstandingRequests);cancelled++;
      }
      return cancelled;
    }
    public void Forget(ulong assetId) { requested.TryRemove(assetId,out _);failed.TryRemove(assetId,out _); }

    private async Task WorkerLoop() {
      CancellationToken token=cancellation.Token;
      int highBurst=0;
      while(!token.IsCancellationRequested) {
        WorldModelStreamRequest request=null;
        // Current-room work stays favoured, but a permanently replenished high queue must not starve direct-neighbour
        // prefetch forever. Jedipedia has explicit priority bands/backpressure; a small weighted schedule gives this
        // two-queue streamer the same essential property. A 4:1 band keeps first-frame/current-room shell work
        // dominant while still guaranteeing neighbour progress.
        if(highBurst<4&&highPriority.TryDequeue(out request))highBurst++;
        else if(normalPriority.TryDequeue(out request))highBurst=0;
        else if(highPriority.TryDequeue(out request))highBurst=1;
        if(request==null) {
          try { await Task.Delay(8,token).ConfigureAwait(false); } catch(OperationCanceledException) { break; }
          continue;
        }
        byte state;
        if(!requested.TryGetValue(request.AssetId,out state)||(state!=0&&state!=1)||!requested.TryUpdate(request.AssetId,2,state))continue;
        GR2 model=null; Exception error=null;
        try {
          string path="/resources/"+(request.AssetPath??String.Empty).Replace('\\','/').TrimStart('/')+".gr2";
          ArchiveFile file=area.FindFile(path);
          if(file==null) throw new FileNotFoundException("World GR2 not found in selected archives.",path);
          using(Stream stream=file.OpenCopyInMemory()) using(var reader=new BinaryReader(stream)) model=new GR2(reader,request.AssetPath,null);
        } catch(Exception ex) { error=ex; }
        if(model!=null)requested[request.AssetId]=3;else {requested.TryRemove(request.AssetId,out _);failed.TryAdd(request.AssetId,0);}
        completed.Enqueue(new WorldModelStreamResult(request,model,error));
      }
    }

    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
  }

  // MAT XML/archive work is intentionally independent from GR2 decoding. Putting ParseMAT() in the GR2 workers
  // delayed the model result itself, so a room could not even upload geometry until every new material referenced
  // by that GR2 had completed extra TOR/XML lookups. Jedipedia keeps GR2 and MAT as separate queues as well.
  // This worker mutates a material only while View_AREA treats it as unavailable; D3D texture creation remains on
  // the render thread after IsSettled() becomes true.
  internal sealed class WorldMaterialMetadataStreamer : IDisposable {
    private readonly ConcurrentQueue<GR2_Material> pending=new ConcurrentQueue<GR2_Material>();
    // 0=queued, 1=worker owns, 2=parsed/terminal success, 3=terminal failure.
    private readonly ConcurrentDictionary<GR2_Material,byte> states=new ConcurrentDictionary<GR2_Material,byte>();
    private readonly CancellationTokenSource cancellation=new CancellationTokenSource();
    private readonly Task[] workers;

    public WorldMaterialMetadataStreamer(int workerCount) {
      workerCount=Math.Max(1,Math.Min(4,workerCount));workers=new Task[workerCount];
      for(int i=0;i<workers.Length;i++)workers[i]=Task.Run(WorkerLoop);
    }

    public bool Request(GR2_Material material) {
      if(material==null)return false;
      if(material.parsed){states[material]=2;return true;}
      if(states.TryGetValue(material,out byte oldState)){
        // Texture eviction resets parsed=false so the MAT can be rehydrated later. Re-arm a previously successful
        // metadata item instead of leaving it permanently stuck in the terminal state.
        if(oldState==2&&states.TryUpdate(material,0,2)){pending.Enqueue(material);return true;}
        return false;
      }
      if(!states.TryAdd(material,0))return false;pending.Enqueue(material);return true;
    }
    public bool IsPending(GR2_Material material)=>material!=null&&states.TryGetValue(material,out byte state)&&(state==0||state==1);
    public bool IsFailed(GR2_Material material)=>material!=null&&states.TryGetValue(material,out byte state)&&state==3;
    public bool IsSettled(GR2_Material material)=>material!=null&&(material.parsed||(states.TryGetValue(material,out byte state)&&(state==2||state==3)));
    public int PendingCount { get { int active=pending.Count;foreach(KeyValuePair<GR2_Material,byte> pair in states)if(pair.Value==1)active++;return active; } }

    private async Task WorkerLoop() {
      CancellationToken token=cancellation.Token;
      while(!token.IsCancellationRequested) {
        if(!pending.TryDequeue(out GR2_Material material)) {
          try{await Task.Delay(8,token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
          continue;
        }
        if(material==null||!states.TryUpdate(material,1,0))continue;
        try {
          if(!material.parsed)material.ParseMAT(null,null,0);
          states[material]=2;
        } catch(Exception ex) {
          System.Diagnostics.Debug.WriteLine("World MAT metadata '"+(material.materialName??String.Empty)+"' failed: "+ex.Message);
          states[material]=3;
        }
      }
    }

    public void Dispose(){cancellation.Cancel();cancellation.Dispose();}
  }
}
