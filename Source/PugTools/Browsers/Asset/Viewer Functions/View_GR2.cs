using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDX.DXGI;
using SlimDXNet;
using SlimDXNet.Camera;
using SlimDXNet.FX;
using SlimDXNet.Vertex;
using Buffer = SlimDX.Direct3D11.Buffer;
using MathF = SlimDXNet.MathF;

namespace PugTools {
  class ViewGR2 : D3DPanelApp {
    private Vector3 _cameraPos;
    private Matrix _cMatrix;
    private Boolean _disposed;
    // private Single _flyingCameraSpeed; // = 15.0f;
    private GR2_Effect _fx;
    private Vector3 _globalBoxCenter;
    private Vector3 _globalBoxMax;
    private Vector3 _globalBoxMin;
    // private readonly List<UInt16> _indexList = new List<UInt16>();
    // private readonly List<GR2_Mesh_Vertex_Index> _indices = new List<GR2_Mesh_Vertex_Index>();
    private Point _lastMousePos;
    private Boolean _makeScreenshot;
    private GR2 _model;
    private JBAAnimation _jbaAnimation;
    private JBARig _jbaRig;

    // Files prefixed with ad_ are Morpheme additive overlays, not complete
    // poses. Jedipedia layers them over ex_stand_idle_1/ex_idle_1 when that
    // sibling clip exists, otherwise over the GR2 bind pose. Keep the base
    // layer separate because it has its own duration/frame rate and loops on
    // its own timeline.
    private Boolean _jbaIsAdditive;
    private JBAAnimation _jbaBaseAnimation;
    private JBARig _jbaBaseRig;
    private Single _jbaBaseTime;
    private Double _jbaBasePlaybackStartTime;

    private Dictionary<String, Matrix> _jbaSkinMatrices =
      new Dictionary<String, Matrix>(StringComparer.OrdinalIgnoreCase);

    // Reused for every JBA frame. Avoid allocating large managed arrays
    // while the render thread is running.
    private readonly Dictionary<GR2_Mesh, Matrix[]> _jbaPaletteCache =
      new Dictionary<GR2_Mesh, Matrix[]>();

    private readonly Dictionary<GR2_Mesh, PosNormalTexTan[]> _jbaSkinnedCache =
      new Dictionary<GR2_Mesh, PosNormalTexTan[]>();

    private readonly Dictionary<GR2_Mesh, Buffer> _jbaGpuVertexBuffers =
      new Dictionary<GR2_Mesh, Buffer>();
    private Single _jbaTime;
    private Int32 _jbaFrame = -1;
    private Boolean _jbaPlaying;
    private Boolean _jbaLoop = true;
    private Single _jbaPlaybackSpeed = 1.0F;

    // JBA playback is driven from an absolute high-resolution clock instead of
    // accumulating render-loop deltas. This mirrors Jedipedia's wall-clock
    // playback and prevents a slow render/GC frame from permanently skewing
    // animation timing.
    private readonly Stopwatch _jbaPlaybackClock = new Stopwatch();
    private Double _jbaPlaybackStartTime;

    // Everything below is reused for the lifetime of the loaded JBA/model pair.
    // The old hot path allocated dictionaries/arrays and repeatedly rebuilt
    // matrices on every redraw. Keeping bind/inverse-bind and mapping data here
    // removes that pressure while still using the real GR2 bind pose.
    private JBATransform[] _jbaPoseSamples;
    private Int32[] _jbaSkeletonToChannel;
    private Boolean[] _jbaUseBindTranslation;

    // Diagnostic counts reflect the FINAL name binding against the actual GR2
    // skeleton, not merely the raw RigToAnimMap entry count. This makes a
    // misleading "102/102" MPH map distinguishable from a map whose rig bone
    // names really drive the loaded body.
    private Int32 _jbaBoundSkeletonBoneCount;
    private Int32 _jbaBoundChannelCount;
    private Int32 _jbaBaseBoundSkeletonBoneCount;
    private Int32 _jbaBaseBoundChannelCount;

    // Additive-base equivalents. The overlay deliberately does NOT use the
    // constant-translation substitution: its translations are deltas and zero
    // must remain zero. The substitution belongs only to a full/base pose.
    private JBATransform[] _jbaBasePoseSamples;
    private Int32[] _jbaBaseSkeletonToChannel;
    private Boolean[] _jbaBaseUseBindTranslation;

    // Retained for compatibility/debugging with the older Hero-space pose
    // helpers.  The active JBA path below composes the corresponding bind
    // rotation/translation in Morpheme space instead.
    private Matrix[] _jbaBindLocal;
    private Matrix[] _jbaBindLocalFull;
    private Matrix[] _jbaInverseBind;
    private Matrix[] _jbaCurrentWorld;
    private Boolean[] _jbaCurrentValid;

    // Exact Jedipedia/Morpheme pose state.  Bind rotations recovered from a
    // GR2 rootToBone matrix are not necessarily unit quaternions (exporter
    // scale/shear is intentionally left in them by the old gl-matrix
    // getRotation() used by Jedipedia).  Matrix-multiplying those locals is
    // NOT equivalent to Jedipedia's quat.mul()/vec3.transformQuat() path.
    // Keep the pose in Morpheme quaternion/vector form until the final skin
    // matrix so rigs such as Ithorian reproduce the reader exactly.
    private System.Numerics.Quaternion[] _jbaBindRotationMorpheme;
    private System.Numerics.Vector3[] _jbaBindTranslationMorpheme;
    private System.Numerics.Quaternion[] _jbaWorldRotationMorpheme;
    private System.Numerics.Vector3[] _jbaWorldTranslationMorpheme;

    private Buffer _jbaBoneBuffer;
    private Int32 _jbaBoneVertexCount;
    private Int32 _jbaBoneBufferCapacity;
    private PosNormalTexTan[] _jbaBoneVertices;
    private volatile Boolean _showSkeleton;

    // JBA bone labels are rendered through a viewer-local D3D11 text atlas.
    // This avoids depending on SpriteTextRenderer inside D3DPanelApp (where the
    // previous label path never produced visible output) and keeps the labels
    // in the same swap chain as the model/skeleton overlay.
    private ShaderResourceView _jbaLabelTexture;
    private Buffer _jbaLabelBuffer;
    private PosNormalTexTan[] _jbaLabelVertices;
    private Vector4[] _jbaLabelUvRects;
    private Vector2[] _jbaLabelPixelSizes;
    private Int32 _jbaLabelBufferCapacity;
    private Int32 _jbaLabelVertexCount;
    private GR2_Material _jbaMaterial;
    private readonly LookAtCamera _camera;
    private Single _cameraZoomSpeed; // = 0.40f;
    private Matrix _pMatrix;
    private List<PosNormalTexTan> _vertices; // = new List<PosNormalTexTan>();

    internal ViewGR2(IntPtr hInstance, Form form, String panelName = "")
      : base(hInstance, panelName) {

      Window = form;
      RenderPanelName = panelName;
      Enable4XMsaa = true;

      ClientHeight = form.Controls.Find(panelName, true).First().Height;
      ClientWidth = form.Controls.Find(panelName, true).First().Width;

      _camera = new LookAtCamera();
      _lastMousePos = new Point();
    }
    private static Byte JbaBoneIndexByte(Single normalizedIndex) {
      Int32 value = (Int32)Math.Round(normalizedIndex * 255.0F);
      if (value < 0) value = 0;
      if (value > 255) value = 255;
      return (Byte)value;
    }

    private void ReleaseJbaGpuVertexBuffers() {
      foreach (Buffer stored in _jbaGpuVertexBuffers.Values) {
        Buffer buffer = stored;
        Util.ReleaseCom(ref buffer);
      }

      _jbaGpuVertexBuffers.Clear();
    }

    private EffectTechnique GetGpuSkinnedTechnique(EffectTechnique technique) {
      if (technique == _fx.Eye) return _fx.EyeSkinned;
      if (technique == _fx.Garment) return _fx.GarmentSkinned;
      if (technique == _fx.HairC) return _fx.HairCSkinned;
      if (technique == _fx.SkinB) return _fx.SkinBSkinned;
      return _fx.GenericSkinned;
    }

    private Boolean PrepareGpuSkinning(
      GR2_Mesh mesh,
      out Buffer vertexBuffer
    ) {
      vertexBuffer = null;

      if (_jbaAnimation == null
          || _fx == null
          || InputLayouts.PosNormalTexTanSkinned == null
          || mesh == null
          || mesh.meshBones == null
          || mesh.meshBones.Count == 0
          || mesh.meshBones.Count > 256
          || !_jbaGpuVertexBuffers.TryGetValue(mesh, out vertexBuffer)) {
        return false;
      }

      if (!_jbaPaletteCache.TryGetValue(mesh, out Matrix[] palette)
          || palette.Length != mesh.meshBones.Count) {
        palette = new Matrix[mesh.meshBones.Count];
        _jbaPaletteCache[mesh] = palette;
      }

      for (Int32 i = 0; i < palette.Length; i++) {
        String boneName = CanonicalAnimationBoneName(
          mesh.meshBones[i].boneName
        );

        palette[i] = _jbaSkinMatrices.TryGetValue(
          boneName,
          out Matrix skin
        )
          ? skin
          : Matrix.Identity;
      }

      _fx.SetSkinPalette(palette);
      return true;
    }

    private void BuildGeometry() {
      if (_model.numMeshes > 0) {
        foreach (GR2_Mesh mesh in _model.meshes) {
          _vertices = new List<PosNormalTexTan>();

          if (mesh.meshName.Contains("collision")) continue;

          foreach (GR2_Mesh_Vertex vertex in mesh.meshVerts) {
            Vector3 pos = new Vector3(vertex.X, vertex.Y, vertex.Z);
            Vector3 norm = new Vector3(vertex.normX, vertex.normY, vertex.normZ);
            Vector2 texC = new Vector2(vertex.texU, vertex.texV);
            Vector3 tan = new Vector3(vertex.tanX, vertex.tanY, vertex.tanZ);
            _vertices.Add(new PosNormalTexTan(pos, norm, texC, tan));
          }

          BufferDescription vbd = new BufferDescription(
            PosNormalTexTan.Stride * _vertices.Count,
            ResourceUsage.Dynamic,
            BindFlags.VertexBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0
          );
          mesh.vertBuffer = new Buffer(
            Device,
            new DataStream(_vertices.ToArray(), false, false),
            vbd
          );

          if (mesh.meshBones != null
              && mesh.meshBones.Count > 0
              && mesh.meshBones.Count <= 256) {

            PosNormalTexTanSkinned[] gpuVertices =
              new PosNormalTexTanSkinned[mesh.meshVerts.Count];

            for (Int32 i = 0; i < mesh.meshVerts.Count; i++) {
              GR2_Mesh_Vertex vertex = mesh.meshVerts[i];

              gpuVertices[i] = new PosNormalTexTanSkinned(
                new Vector3(vertex.X, vertex.Y, vertex.Z),
                new Vector3(vertex.normX, vertex.normY, vertex.normZ),
                new Vector2(vertex.texU, vertex.texV),
                new Vector3(vertex.tanX, vertex.tanY, vertex.tanZ),
                new Vector4(
                  vertex.boneWeight1,
                  vertex.boneWeight2,
                  vertex.boneWeight3,
                  vertex.boneWeight4
                ),
                JbaBoneIndexByte(vertex.boneIndex1),
                JbaBoneIndexByte(vertex.boneIndex2),
                JbaBoneIndexByte(vertex.boneIndex3),
                JbaBoneIndexByte(vertex.boneIndex4)
              );
            }

            BufferDescription skinVbd = new BufferDescription(
              PosNormalTexTanSkinned.Stride * gpuVertices.Length,
              ResourceUsage.Immutable,
              BindFlags.VertexBuffer,
              CpuAccessFlags.None,
              ResourceOptionFlags.None,
              0
            );

            _jbaGpuVertexBuffers[mesh] = new Buffer(
              Device,
              new DataStream(gpuVertices, false, false),
              skinVbd
            );
          }

          UInt16[] indexArray = mesh.meshVertIndex.Select(
            GR2_Mesh_Vertex_Index => GR2_Mesh_Vertex_Index.index
          ).ToArray();
          BufferDescription ibd = new BufferDescription(
            sizeof(UInt16) * indexArray.Length,
            ResourceUsage.Immutable,
            BindFlags.IndexBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
          );
          mesh.idxBuffer = new Buffer(Device, new DataStream(indexArray, false, false), ibd);
        }
      }
    }
    internal void Clear() {
      StopRender();
      if (_model != null) {
        foreach (var mesh in _model.meshes) {
          Util.ReleaseCom(ref mesh.idxBuffer);
          Util.ReleaseCom(ref mesh.vertBuffer);
        }

        foreach (var mat in _model.materials) {
          Util.ReleaseCom(ref mat.diffuseSRV);
          Util.ReleaseCom(ref mat.rotationSRV);
          Util.ReleaseCom(ref mat.glossSRV);
          Util.ReleaseCom(ref mat.paletteMaskSRV);
          Util.ReleaseCom(ref mat.paletteSRV);
          Util.ReleaseCom(ref mat.complexionSRV);
          Util.ReleaseCom(ref mat.facepaintSRV);
          Util.ReleaseCom(ref mat.ageSRV);
        }
        _model.Dispose();
      }
      Util.ReleaseCom(ref _jbaBoneBuffer);
      ReleaseJbaGpuVertexBuffers();
      _jbaAnimation = null;
      _jbaRig = null;
      _jbaSkinMatrices.Clear();
      ReleaseJbaGpuVertexBuffers();
      _jbaPaletteCache.Clear();
      _jbaSkinnedCache.Clear();
      _jbaFrame = -1;
      _jbaBoneVertexCount = 0;
      _jbaBoneBufferCapacity = 0;
      _jbaBoneVertices = null;
      _showSkeleton = false;
      if (_jbaMaterial != null) {
        Util.ReleaseCom(ref _jbaMaterial.diffuseSRV);
        Util.ReleaseCom(ref _jbaMaterial.rotationSRV);
        Util.ReleaseCom(ref _jbaMaterial.glossSRV);
        _jbaMaterial = null;
      }
      if (_vertices != null) _vertices.Clear();
      // _indices.Clear();
      // _indexList.Clear();
    }
    protected override void Dispose(Boolean disposing) {
      if (!_disposed) {
        if (disposing) {
          Window = null;
          if (_model != null) {
            foreach (var mesh in _model.meshes) {
              Util.ReleaseCom(ref mesh.idxBuffer);
              Util.ReleaseCom(ref mesh.vertBuffer);
            }

            foreach (var mat in _model.materials) {
              Util.ReleaseCom(ref mat.diffuseSRV);
              Util.ReleaseCom(ref mat.rotationSRV);
              Util.ReleaseCom(ref mat.glossSRV);
              Util.ReleaseCom(ref mat.paletteMaskSRV);
              Util.ReleaseCom(ref mat.paletteSRV);
              Util.ReleaseCom(ref mat.complexionSRV);
              Util.ReleaseCom(ref mat.facepaintSRV);
              Util.ReleaseCom(ref mat.ageSRV);
            }
            _model.Dispose();
          }

          Util.ReleaseCom(ref _jbaBoneBuffer);
          ReleaseJbaGpuVertexBuffers();
          _jbaPaletteCache.Clear();
          _jbaSkinnedCache.Clear();

          if (_jbaMaterial != null) {
            Util.ReleaseCom(ref _jbaMaterial.diffuseSRV);
            Util.ReleaseCom(ref _jbaMaterial.rotationSRV);
            Util.ReleaseCom(ref _jbaMaterial.glossSRV);
            _jbaMaterial = null;
          }

          Util.ReleaseCom(ref _jbaLabelBuffer);
          Util.ReleaseCom(ref _jbaLabelTexture);
          _jbaLabelVertices = null;
          _jbaLabelUvRects = null;
          _jbaLabelPixelSizes = null;
          _jbaLabelBufferCapacity = 0;
          _jbaLabelVertexCount = 0;

          Effects.DestroyAll();
          InputLayouts.DestroyAll();
          RenderStates.DestroyAll();

          _fx.Dispose();
          if (_vertices != null) _vertices.Clear();
          // _indices.Clear();
          // _indexList.Clear();
        }
        _disposed = true;
      }
      base.Dispose(disposing);
    }
    private EffectTechnique ApplyMaterial(
      GR2_Material selectedMaterial,
      EffectTechnique activeTech
    ) {
      if (selectedMaterial == null)
        return activeTech ?? _fx.Generic;

      if (activeTech == null)
        activeTech = _fx.Generic;

      switch (selectedMaterial.derived) {
        case "Eye":
          activeTech = _fx.Eye;
          break;
        case "Garment":
          activeTech = _fx.Garment;
          break;
        case "HairC":
          activeTech = _fx.HairC;
          break;
        case "SkinB":
          activeTech = _fx.SkinB;
          break;
        default:
          activeTech = _fx.Generic;
          break;
      }

      switch (selectedMaterial.alphaMode) {
        case "Test":
          _fx.SetAlphaMode(1);
          break;
        case "Add":
          _fx.SetAlphaMode(2);
          break;
        case "Multiply":
          _fx.SetAlphaMode(3);
          break;
        case "Full":
        case "MultiPassFull":
          _fx.SetAlphaMode(4);
          break;
        default:
          _fx.SetAlphaMode(0);
          break;
      }

      _fx.SetAlphaTestValue(selectedMaterial.alphaTestValue);

      if (selectedMaterial.isTwoSided)
        ImmediateContext.Rasterizer.State = RenderStates.TwoSidedRS;
      else
        ImmediateContext.Rasterizer.State = RenderStates.OneSidedRS;

      // A missing diffuse SRV previously produced a completely black model.
      // The neutral JBA material is preferable to a black silhouette while
      // keeping every other material input intact.
      ShaderResourceView diffuse = selectedMaterial.diffuseSRV;
      if (diffuse == null && _jbaMaterial != null)
        diffuse = _jbaMaterial.diffuseSRV;

      _fx.SetDiffuseMap(diffuse);
      _fx.SetRotationMap(selectedMaterial.rotationSRV);
      _fx.SetGlossMap(selectedMaterial.glossSRV);
      _fx.SetPaletteMap(selectedMaterial.paletteSRV);
      _fx.SetPaletteMaskMap(selectedMaterial.paletteMaskSRV);
      _fx.SetComplexionMap(selectedMaterial.complexionSRV);
      _fx.SetFacepaintMap(selectedMaterial.facepaintSRV);
      _fx.SetAgeMap(selectedMaterial.ageSRV);

      _fx.SetPalette1(selectedMaterial.palette1);
      _fx.SetPalette2(selectedMaterial.palette2);
      _fx.SetPalette1Spec(selectedMaterial.palette1Spec);
      _fx.SetPalette2Spec(selectedMaterial.palette2Spec);
      _fx.SetPalette1MetSpec(selectedMaterial.palette1MetSpec);
      _fx.SetPalette2MetSpec(selectedMaterial.palette2MetSpec);
      _fx.SetFlushTone(selectedMaterial.flushTone);
      _fx.SetFleshBrightness(selectedMaterial.fleshBrightness);

      return activeTech;
    }

    public override void DrawScene() {
      base.DrawScene();

      ImmediateContext.ClearRenderTargetView(RenderTargetView, Color.LightSteelBlue);
      ImmediateContext.ClearDepthStencilView(
        DepthStencilView,
        DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil,
        1.0F,
        0
      );

      ImmediateContext.InputAssembler.InputLayout = InputLayouts.PosNormalTexTan;
      ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
      ImmediateContext.OutputMerger.BlendState = RenderStates.AlphaToCoverageBS;
      ImmediateContext.Rasterizer.State = RenderStates.OneSidedRS;

      _camera.UpdateViewMatrix();
      _cMatrix = _camera.View;
      _pMatrix = _camera.Proj;

      if (_fx == null) {
        System.Diagnostics.Debug.WriteLine("ViewGR2: DrawScene skipped because GR2 effect is unavailable.");
        SwapChain.Present(1, PresentFlags.None);
        return;
      }

      EffectTechnique activeTech = _fx.Generic;

      // Pose and update the dynamic vertex buffers before drawing the mesh.
      // The previous version rebuilt them from DrawAnimationSkeleton(), after
      // the triangles had already been submitted.
      if (_jbaAnimation != null) {
        Int32 expectedFrame = _jbaAnimation.FPS > 0
    ? Math.Min(
        _jbaAnimation.FrameCount - 1,
        (Int32)Math.Floor(
          _jbaTime * _jbaAnimation.FPS
        )
      )
    : 0;

        // GPU skinning only uploads bone matrices. Rebuild the pose every
        // render frame while playing so JBA frame-to-frame interpolation is
        // visible instead of snapping at the source FPS.
        if (_jbaPlaying
            || expectedFrame != _jbaFrame) {
          RebuildAnimationSkeletonBuffer();
        }
      }

      if (Form.ActiveForm != null) {
        if (Util.IsKeyDown(Keys.C)) {
          ImmediateContext.Rasterizer.State = RenderStates.WireframeNoneRS;
        }

        if (Util.IsKeyDown(Keys.PrintScreen)) {
          _makeScreenshot = true;
        }

        if (Util.IsKeyDown(Keys.D1)) {
          activeTech = _fx.filterDiffuseMap;
        }

        if (Util.IsKeyDown(Keys.D2)) {
          activeTech = _fx.filterSpecular;
        }

        if (Util.IsKeyDown(Keys.D3)) {
          activeTech = _fx.filterEmissive;
        }

        // if (Util.IsKeyDown(Keys.D4))
        // {
        //     activeTech = _fx.Light1UberAmbient;
        // }
      }

      Matrix mvMatrix = Matrix.Identity;

      Matrix.Multiply(ref mvMatrix, ref _cMatrix, out mvMatrix);
      Matrix.Multiply(ref mvMatrix, ref _pMatrix, out Matrix wvp);
      Matrix.Invert(ref mvMatrix, out mvMatrix);
      Matrix.Transpose(ref mvMatrix, out mvMatrix);

      _fx.SetWorldMatrix(mvMatrix);
      _fx.SetMvMatrix(wvp);

      if (_model == null) {
        SwapChain.Present(1, PresentFlags.None);
        return;
      }

      foreach (GR2_Mesh mesh in _model.meshes) {
        if (mesh.meshName.Contains("collision")) continue;

        Boolean gpuSkinned = PrepareGpuSkinning(
          mesh,
          out Buffer drawVertexBuffer
        );

        if (gpuSkinned) {
          ImmediateContext.InputAssembler.InputLayout =
            InputLayouts.PosNormalTexTanSkinned;

          ImmediateContext.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(
              drawVertexBuffer,
              PosNormalTexTanSkinned.Stride,
              0
            )
          );
        }
        else {
          ImmediateContext.InputAssembler.InputLayout =
            InputLayouts.PosNormalTexTan;

          ImmediateContext.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(
              mesh.vertBuffer,
              PosNormalTexTan.Stride,
              0
            )
          );
        }

        ImmediateContext.InputAssembler.SetIndexBuffer(
          mesh.idxBuffer,
          Format.R16_UInt,
          0
        );

        foreach (GR2_Mesh_Piece piece in mesh.meshPieces) {
          EffectTechnique pieceTech = activeTech;

          if (piece.matId >= 0 && piece.matId < _model.materials.Count) {
            pieceTech = ApplyMaterial(
              _model.materials[piece.matId],
              pieceTech
            );
          }
          else if (_model.materials.Count > 0) {
            pieceTech = ApplyMaterial(
              _model.materials[0],
              pieceTech
            );
          }
          else if (_jbaMaterial != null) {
            pieceTech = ApplyMaterial(
              _jbaMaterial,
              pieceTech
            );
          }

          if (gpuSkinned)
            pieceTech = GetGpuSkinnedTechnique(pieceTech);

          pieceTech.GetPassByIndex(0).Apply(ImmediateContext);

          ImmediateContext.DrawIndexed(
            ((Int32)piece.numPieceFaces) * 3,
            ((Int32)piece.startIndex) * 3,
            0
          );
        }
      }

      if (_showSkeleton) {
        DrawAnimationSkeleton(activeTech);
        DrawAnimationSkeletonLabels();
      }

      SwapChain.Present(1, PresentFlags.None);

      if (_makeScreenshot) {
        MakeScreenshot(ImageFileFormat.Png);
        _makeScreenshot = false;
      }
    }
    public override Boolean Init() {
      if (!base.Init()) return false;

      Effects.InitAll(Device);
      _fx = Effects.GR2_FX;
      if (_fx == null) {
        System.Diagnostics.Debug.WriteLine("ViewGR2: GR2 effect could not be initialized.");
        return false;
      }
      InputLayouts.InitAll(Device);
      RenderStates.InitAll(Device);

      return true;
    }
    internal void LoadModel(GR2 model = null) {
      if (model != null) _model = model;

      _jbaPaletteCache.Clear();
      _jbaSkinnedCache.Clear();

      _globalBoxMin = new Vector3(
        model.globalBox.minX,
        model.globalBox.minY,
        model.globalBox.minZ
      );
      _globalBoxMax = new Vector3(
        model.globalBox.maxX,
        model.globalBox.maxY,
        model.globalBox.maxZ
      );

      _globalBoxCenter = _globalBoxMin + (_globalBoxMax - _globalBoxMin) / 2;

      Vector3 boxSize = _globalBoxMax - _globalBoxMin;
      Single largestExtent = Math.Max(
        0.01F,
        Math.Max(boxSize.X, Math.Max(boxSize.Y, boxSize.Z))
      );

      _cameraPos = _globalBoxCenter + new Vector3(
        0.0F,
        0.0F,
        largestExtent * 3.6F
      );

      _camera.Reset();
      _camera.Position = _cameraPos;
      _camera.LookAt(_cameraPos, _globalBoxCenter, Vector3.UnitY);

      if (model.numMaterials > 0) {
        foreach (GR2_Material mat in model.materials) {
          mat.ParseMAT(Device);
        }
      }

      BuildGeometry();
    }
    internal void LoadAnimation(
      JBAAnimation animation,
      JBARig rig = null,
      JBAAnimation baseAnimation = null,
      JBARig baseRig = null,
      Boolean additive = false
    ) {
      _jbaPlaybackClock.Stop();
      _jbaRig = rig;
      _jbaAnimation = animation;
      _jbaIsAdditive = additive && animation != null;
      _jbaBaseAnimation = _jbaIsAdditive ? baseAnimation : null;
      _jbaBaseRig = _jbaIsAdditive ? baseRig : null;

      // Front-load discrete JBA decoding once. Playback then interpolates
      // between cached source frames without allocating a new pose per redraw.
      if (_jbaAnimation != null)
        _jbaAnimation.PrepareSamples();
      if (_jbaBaseAnimation != null)
        _jbaBaseAnimation.PrepareSamples();

      _jbaTime = 0.0F;
      _jbaBaseTime = 0.0F;
      _jbaFrame = -1;
      _jbaPlaybackStartTime = 0.0;
      _jbaBasePlaybackStartTime = 0.0;
      _jbaPlaying = animation != null;
      _jbaPlaybackSpeed = 1.0F;
      Util.ReleaseCom(ref _jbaBoneBuffer);
      _jbaBoneVertexCount = 0;
      _jbaBoneBufferCapacity = 0;
      _jbaBoneVertices = null;
      _showSkeleton = false;

      Util.ReleaseCom(ref _jbaLabelBuffer);
      Util.ReleaseCom(ref _jbaLabelTexture);
      _jbaLabelVertices = null;
      _jbaLabelUvRects = null;
      _jbaLabelPixelSizes = null;
      _jbaLabelBufferCapacity = 0;
      _jbaLabelVertexCount = 0;

      BuildJbaBindingCache();
      BuildAnimationSkeletonLabelAtlas();

      if (_jbaMaterial == null) {
        try {
          _jbaMaterial = new GR2_Material("all_test_grey_128");
          _jbaMaterial.ParseMAT(Device);
        } catch {
          _jbaMaterial = null;
        }
      }
    }

    internal Boolean AnimationPlaying => _jbaPlaying;
    internal Single AnimationTime => _jbaTime;
    internal Single AnimationLength => _jbaAnimation?.Length ?? 0.0F;
    internal Int32 AnimationFrame => _jbaFrame < 0 ? 0 : _jbaFrame;
    internal Int32 AnimationFrameCount => _jbaAnimation?.FrameCount ?? 1;
    internal Int32 AnimationBoundSkeletonBoneCount => _jbaBoundSkeletonBoneCount;
    internal Int32 AnimationBoundChannelCount => _jbaBoundChannelCount;
    internal Int32 AnimationSkeletonBoneCount => _model?.skeleton_bones?.Count ?? 0;
    internal Int32 AnimationBaseBoundSkeletonBoneCount => _jbaBaseBoundSkeletonBoneCount;
    internal Int32 AnimationBaseBoundChannelCount => _jbaBaseBoundChannelCount;
    internal Boolean ShowSkeleton => _showSkeleton;

    internal void SetShowSkeleton(Boolean show) {
      _showSkeleton = show;

      // Force one pose rebuild when enabling the overlay while paused. The
      // render thread owns the D3D buffers, so the UI thread only flips this
      // flag/frame marker and never touches ImmediateContext directly.
      if (show)
        _jbaFrame = -1;
      else
        _jbaBoneVertexCount = 0;
    }

    internal void PlayAnimation() {
      if (_jbaAnimation == null) return;

      if (_jbaTime >= _jbaAnimation.Length) {
        _jbaTime = 0.0F;
        _jbaBaseTime = 0.0F;
      }

      _jbaPlaybackStartTime = _jbaTime;
      _jbaBasePlaybackStartTime = _jbaBaseTime;
      _jbaPlaybackClock.Restart();
      _jbaPlaying = true;
    }

    internal void PauseAnimation() {
      UpdateJbaPlaybackTime();
      _jbaPlaying = false;
      _jbaPlaybackClock.Stop();
      _jbaPlaybackStartTime = _jbaTime;
      _jbaBasePlaybackStartTime = _jbaBaseTime;
    }

    internal void StopAnimation() {
      _jbaPlaying = false;
      _jbaPlaybackClock.Reset();
      _jbaPlaybackStartTime = 0.0;
      _jbaBasePlaybackStartTime = 0.0;
      _jbaTime = 0.0F;
      _jbaBaseTime = 0.0F;
      _jbaFrame = -1;
    }

    internal void SetAnimationLoop(Boolean loop) {
      Boolean clockWasRunning = _jbaPlaybackClock.IsRunning;
      if (clockWasRunning)
        UpdateJbaPlaybackTime();

      _jbaLoop = loop;
      _jbaPlaybackStartTime = _jbaTime;
      _jbaBasePlaybackStartTime = _jbaBaseTime;

      if (_jbaPlaying && clockWasRunning)
        _jbaPlaybackClock.Restart();
    }

    internal void SetAnimationSpeed(Single speed) {
      speed = Math.Max(0.05F, Math.Min(8.0F, speed));
      Boolean running = _jbaPlaybackClock.IsRunning;
      if (running) UpdateJbaPlaybackTime();
      _jbaPlaybackSpeed = speed;
      _jbaPlaybackStartTime = _jbaTime;
      _jbaBasePlaybackStartTime = _jbaBaseTime;
      if (_jbaPlaying && running) _jbaPlaybackClock.Restart();
    }

    internal void SeekAnimation(Single timeSeconds) {
      if (_jbaAnimation == null) return;
      Single length = Math.Max(0.0F, _jbaAnimation.Length);
      _jbaTime = Math.Max(0.0F, Math.Min(length, timeSeconds));
      if (_jbaBaseAnimation != null && _jbaBaseAnimation.Length > 0.0F)
        _jbaBaseTime = _jbaTime % _jbaBaseAnimation.Length;
      else
        _jbaBaseTime = 0.0F;
      _jbaFrame = -1;
      _jbaPlaybackStartTime = _jbaTime;
      _jbaBasePlaybackStartTime = _jbaBaseTime;
      if (_jbaPlaying) _jbaPlaybackClock.Restart();
      else _jbaPlaybackClock.Reset();
    }

    private void UpdateJbaPlaybackTime() {
      if (!_jbaPlaying
          || _jbaAnimation == null
          || _jbaAnimation.Length <= 0.0F) {
        return;
      }

      // LoadAnimation is called before the render thread starts. Start the
      // wall clock on the first actual render update so file/material loading
      // time is not counted as animation time.
      if (!_jbaPlaybackClock.IsRunning) {
        _jbaPlaybackStartTime = _jbaTime;
        _jbaBasePlaybackStartTime = _jbaBaseTime;
        _jbaPlaybackClock.Restart();
        return;
      }

      Double elapsed = _jbaPlaybackClock.Elapsed.TotalSeconds * _jbaPlaybackSpeed;
      Double time = _jbaPlaybackStartTime + elapsed;
      Double length = _jbaAnimation.Length;

      if (_jbaLoop) {
        time %= length;
        if (time < 0.0)
          time += length;
      }
      else if (time >= length) {
        time = length;
        _jbaPlaying = false;
        _jbaPlaybackClock.Stop();
      }

      _jbaTime = (Single)time;

      // Morpheme layers keep independent clocks. The base idle continues to
      // loop on its own period even when the overlay has a different FPS or
      // duration; deriving this from the overlay frame/time would slowly drift
      // and then jump at every overlay loop.
      if (_jbaIsAdditive
          && _jbaBaseAnimation != null
          && _jbaBaseAnimation.Length > 0.0F) {

        Double baseTime = _jbaBasePlaybackStartTime + elapsed;
        Double baseLength = _jbaBaseAnimation.Length;
        baseTime %= baseLength;
        if (baseTime < 0.0)
          baseTime += baseLength;
        _jbaBaseTime = (Single)baseTime;
      }
    }

    private static Vector3 BoneBindPosition(GR2_Bone_Skeleton bone) {
      Matrix bind = bone.root;
      try { bind.Invert(); } catch { }
      return new Vector3(bind.M41, bind.M42, bind.M43);
    }

    private void UpdateSkinnedGeometry() {
      if (_model == null || _jbaSkinMatrices.Count == 0)
        return;

      foreach (GR2_Mesh mesh in _model.meshes) {
        if (mesh.vertBuffer == null
            || mesh.meshVerts == null
            || mesh.meshVerts.Count == 0
            || mesh.meshBones == null
            || mesh.meshBones.Count == 0)
          continue;

        if (!_jbaPaletteCache.TryGetValue(mesh, out Matrix[] palette)
            || palette.Length != mesh.meshBones.Count) {
          palette = new Matrix[mesh.meshBones.Count];
          _jbaPaletteCache[mesh] = palette;
        }

        Int32 mappedPaletteBones = 0;
        for (Int32 i = 0; i < palette.Length; i++) {
          String boneName = CanonicalAnimationBoneName(
            mesh.meshBones[i].boneName ?? String.Empty
          );

          if (_jbaSkinMatrices.TryGetValue(boneName, out Matrix skin)) {
            palette[i] = skin;
            mappedPaletteBones++;
          } else {
            palette[i] = Matrix.Identity;
          }
        }


        if (!_jbaSkinnedCache.TryGetValue(mesh, out PosNormalTexTan[] skinned)
            || skinned.Length != mesh.meshVerts.Count) {
          skinned = new PosNormalTexTan[mesh.meshVerts.Count];
          _jbaSkinnedCache[mesh] = skinned;
        }

        for (Int32 i = 0; i < mesh.meshVerts.Count; i++) {
          GR2_Mesh_Vertex src = mesh.meshVerts[i];

          Vector3 originalPos = new Vector3(src.X, src.Y, src.Z);
          Vector3 originalNormal = new Vector3(
            (src.normX - 127.5F) / 127.5F,
            (src.normY - 127.5F) / 127.5F,
            (src.normZ - 127.5F) / 127.5F
          );

                    Vector3 originalTangent = new Vector3(
            (src.tanX - 127.5F) / 127.5F,
            (src.tanY - 127.5F) / 127.5F,
            (src.tanZ - 127.5F) / 127.5F
          );

          Vector3 pos = Vector3.Zero;
          Vector3 norm = Vector3.Zero;
          Vector3 tan = Vector3.Zero;
          Single totalWeight = 0.0F;

          // IMPORTANT: Do not allocate weight/index arrays here.
          // This loop runs once for every vertex of every animation frame.
          // The old code created TWO managed arrays per vertex, producing
          // massive Gen0 GC pressure and visible periodic animation stalls.
          Single weight;
          Int32 boneIndex;
          Matrix skin;

          weight = src.boneWeight1;
          if (weight > 0.00001F) {
            boneIndex = (Int32)Math.Round(src.boneIndex1 * 255.0F);
            if (boneIndex >= 0 && boneIndex < palette.Length) {
              skin = palette[boneIndex];
              pos += Vector3.TransformCoordinate(originalPos, skin) * weight;
              totalWeight += weight;
            }
          }

          weight = src.boneWeight2;
          if (weight > 0.00001F) {
            boneIndex = (Int32)Math.Round(src.boneIndex2 * 255.0F);
            if (boneIndex >= 0 && boneIndex < palette.Length) {
              skin = palette[boneIndex];
              pos += Vector3.TransformCoordinate(originalPos, skin) * weight;
              totalWeight += weight;
            }
          }

          weight = src.boneWeight3;
          if (weight > 0.00001F) {
            boneIndex = (Int32)Math.Round(src.boneIndex3 * 255.0F);
            if (boneIndex >= 0 && boneIndex < palette.Length) {
              skin = palette[boneIndex];
              pos += Vector3.TransformCoordinate(originalPos, skin) * weight;
              totalWeight += weight;
            }
          }

          weight = src.boneWeight4;
          if (weight > 0.00001F) {
            boneIndex = (Int32)Math.Round(src.boneIndex4 * 255.0F);
            if (boneIndex >= 0 && boneIndex < palette.Length) {
              skin = palette[boneIndex];
              pos += Vector3.TransformCoordinate(originalPos, skin) * weight;
              totalWeight += weight;
            }
          }

          if (totalWeight <= 0.00001F) {
            pos = originalPos;
          } else if (Math.Abs(totalWeight - 1.0F) > 0.0001F) {
            pos /= totalWeight;
          }

          // Fast preview path: keep the original vertex basis. Updating
          // normals/tangents through up to four skin matrices costs roughly
          // twice as much as the position skinning itself and is not required
          // for a usable animation preview.
          norm = originalNormal;
          tan = originalTangent;

          if (norm.LengthSquared() > 0.000001F) norm.Normalize();
          if (tan.LengthSquared() > 0.000001F) tan.Normalize();

          skinned[i] = new PosNormalTexTan(
            pos,
            norm,
            new Vector2(src.texU, src.texV),
            tan
          );
        }

        try {
          DataBox mapped = ImmediateContext.MapSubresource(
            mesh.vertBuffer,
            MapMode.WriteDiscard,
            SlimDX.Direct3D11.MapFlags.None
          );
          mapped.Data.WriteRange(skinned);
          ImmediateContext.UnmapSubresource(mesh.vertBuffer, 0);
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA CPU skinning update failed: " + ex.Message
          );
        }
      }
    }

    private static Matrix PoseToModelMatrix(
      SlimDX.Quaternion morphemeRotation,
      Vector3 morphemeTranslation
    ) {
      SlimDX.Quaternion q = new SlimDX.Quaternion(
        morphemeRotation.X,
        morphemeRotation.Z,
        -morphemeRotation.Y,
        morphemeRotation.W
      );

      Matrix matrix = Matrix.RotationQuaternion(q);
      matrix.M41 = morphemeTranslation.X * 0.001F;
      matrix.M42 = morphemeTranslation.Z * 0.001F;
      matrix.M43 = -morphemeTranslation.Y * 0.001F;
      matrix.M44 = 1.0F;
      return matrix;
    }

    private static Vector3 MorphemeToModel(Vector3 p) {
      // Jedipedia jbaBoneToModelSpace:
      // Morpheme centimetres -> HeroEngine/GR2 metres and axis swap.
      return new Vector3(
        0.001F * p.X,
        0.001F * p.Z,
        -0.001F * p.Y
      );
    }

    private static String CanonicalAnimationBoneName(String name) {
      if (String.Equals(name, "Bip01", StringComparison.OrdinalIgnoreCase))
        return "GOD";

      return name ?? String.Empty;
    }

    private static Matrix InverseMatrix(Matrix matrix) {
      try {
        matrix.Invert();
        return matrix;
      }
      catch {
        return Matrix.Identity;
      }
    }

    // Jedipedia's jbaSkeletonBindLocals() extracts only rotation and
    // translation from a GR2 bind-local before using it as an animation local.
    // This is important for humanoid face/helper bones: exporter scale/shear in
    // the bind matrix belongs to the inverse-bind relation, not to the animated
    // local chain. Carrying that scale forward every frame crumples the head.
    // Keep the exact bind-world separately for inverse skinning; pose locals are
    // deliberately rigid transforms.
    private static Matrix HeroToMorphemeBasis() {
      // Row-vector form of Jedipedia's morphemeSpaceMatrix. Hero/GR2 uses
      // decametres with Y-up; Morpheme/JBA uses centimetres with Z-up.
      return new Matrix {
        M11 = 1000.0F,
        M23 = 1000.0F,
        M32 = -1000.0F,
        M44 = 1.0F
      };
    }

    private static Matrix MorphemeToHeroBasis() {
      return new Matrix {
        M11 = 0.001F,
        M23 = -0.001F,
        M32 = 0.001F,
        M44 = 1.0F
      };
    }

    // Jedipedia's jbaSkeletonBindLocals() FIRST converts the complete GR2
    // parent-relative matrix into Morpheme space and only THEN calls the
    // version of gl-matrix mat4.getRotation() shipped with the reader.
    //
    // A subtle but important detail: that old gl-matrix implementation does
    // NOT perform a Matrix.Decompose(), does NOT orthogonalize scale/shear and
    // does NOT normalize the resulting quaternion. SlimDX Decompose therefore
    // produces a different bind rotation for exporter matrices that contain
    // scale/shear. Most creature rigs hide the difference; Ithorian does not.
    //
    // This is the row-vector/transposed equivalent of the exact gl-matrix
    // getRotation() formula used by Jedipedia.
    private static Boolean TryJedipediaBindRotation(
      Matrix matrix,
      out SlimDX.Quaternion rotation
    ) {
      rotation = SlimDX.Quaternion.Identity;

      Single m00 = matrix.M11;
      Single m11 = matrix.M22;
      Single m22 = matrix.M33;
      Single trace = m00 + m11 + m22;
      Single scale;

      try {
        if (trace > 0.0F) {
          scale = 2.0F * (Single)Math.Sqrt(trace + 1.0F);
          if (Math.Abs(scale) <= 0.0000001F) return false;

          rotation.W = 0.25F * scale;
          rotation.X = (matrix.M23 - matrix.M32) / scale;
          rotation.Y = (matrix.M31 - matrix.M13) / scale;
          rotation.Z = (matrix.M12 - matrix.M21) / scale;
        }
        else if (m00 > m11 && m00 > m22) {
          scale = 2.0F * (Single)Math.Sqrt(1.0F + m00 - m11 - m22);
          if (Math.Abs(scale) <= 0.0000001F) return false;

          rotation.W = (matrix.M23 - matrix.M32) / scale;
          rotation.X = 0.25F * scale;
          rotation.Y = (matrix.M12 + matrix.M21) / scale;
          rotation.Z = (matrix.M31 + matrix.M13) / scale;
        }
        else if (m11 > m22) {
          scale = 2.0F * (Single)Math.Sqrt(1.0F + m11 - m00 - m22);
          if (Math.Abs(scale) <= 0.0000001F) return false;

          rotation.W = (matrix.M31 - matrix.M13) / scale;
          rotation.X = (matrix.M12 + matrix.M21) / scale;
          rotation.Y = 0.25F * scale;
          rotation.Z = (matrix.M23 + matrix.M32) / scale;
        }
        else {
          scale = 2.0F * (Single)Math.Sqrt(1.0F + m22 - m00 - m11);
          if (Math.Abs(scale) <= 0.0000001F) return false;

          rotation.W = (matrix.M12 - matrix.M21) / scale;
          rotation.X = (matrix.M31 + matrix.M13) / scale;
          rotation.Y = (matrix.M23 + matrix.M32) / scale;
          rotation.Z = 0.25F * scale;
        }
      }
      catch {
        return false;
      }

      return Single.IsFinite(rotation.X)
        && Single.IsFinite(rotation.Y)
        && Single.IsFinite(rotation.Z)
        && Single.IsFinite(rotation.W);
    }

    private static Matrix JedipediaRotationMatrix(
      SlimDX.Quaternion rotation
    ) {
      // Exact gl-matrix mat4.fromQuat()/fromRotationTranslation() 3x3,
      // transposed into SlimDX row-vector storage. This deliberately consumes
      // a non-unit quaternion verbatim.
      Single x = rotation.X;
      Single y = rotation.Y;
      Single z = rotation.Z;
      Single w = rotation.W;
      Single x2 = x + x;
      Single y2 = y + y;
      Single z2 = z + z;
      Single xx = x * x2;
      Single xy = x * y2;
      Single xz = x * z2;
      Single yy = y * y2;
      Single yz = y * z2;
      Single zz = z * z2;
      Single wx = w * x2;
      Single wy = w * y2;
      Single wz = w * z2;

      return new Matrix {
        M11 = 1.0F - (yy + zz),
        M12 = xy + wz,
        M13 = xz - wy,
        M14 = 0.0F,

        M21 = xy - wz,
        M22 = 1.0F - (xx + zz),
        M23 = yz + wx,
        M24 = 0.0F,

        M31 = xz + wy,
        M32 = yz - wx,
        M33 = 1.0F - (xx + yy),
        M34 = 0.0F,

        M41 = 0.0F,
        M42 = 0.0F,
        M43 = 0.0F,
        M44 = 1.0F
      };
    }

    private static Matrix RigidAnimationLocal(Matrix matrix) {
      Matrix heroToMorpheme = HeroToMorphemeBasis();
      Matrix morphemeToHero = MorphemeToHeroBasis();
      Matrix temp;
      Matrix morpheme;

      // Row-vector transpose of Jedipedia:
      //   localM = H2M_col * localH_col * M2H_col
      // becomes
      //   localM_row = M2H_row * localH_row * H2M_row.
      Matrix.Multiply(ref morphemeToHero, ref matrix, out temp);
      Matrix.Multiply(ref temp, ref heroToMorpheme, out morpheme);

      if (!TryJedipediaBindRotation(
            morpheme,
            out SlimDX.Quaternion rotation)) {
        return matrix;
      }

      // mat4.getTranslation() is simply elements 12/13/14. In SlimDX's
      // transposed row-vector representation those are M41/M42/M43.
      Vector3 translation = new Vector3(
        morpheme.M41,
        morpheme.M42,
        morpheme.M43
      );

      if (!Single.IsFinite(translation.X)
          || !Single.IsFinite(translation.Y)
          || !Single.IsFinite(translation.Z)) {
        return matrix;
      }

      // Deliberately do NOT normalize `rotation`: Jedipedia's gl-matrix 2.x
      // getRotation() does not either, and mat4.fromRotationTranslation()
      // consumes that quaternion verbatim.
      Matrix rigidMorpheme = JedipediaRotationMatrix(rotation);
      rigidMorpheme.M41 = translation.X;
      rigidMorpheme.M42 = translation.Y;
      rigidMorpheme.M43 = translation.Z;
      rigidMorpheme.M44 = 1.0F;

      // Convert the extracted animation-local representation back to Hero
      // row-vector space so the existing renderer can keep its proven
      // skinning path (inverseBind * animatedWorld).
      Matrix rigidHeroTemp;
      Matrix rigidHero;
      Matrix.Multiply(
        ref heroToMorpheme,
        ref rigidMorpheme,
        out rigidHeroTemp
      );
      Matrix.Multiply(
        ref rigidHeroTemp,
        ref morphemeToHero,
        out rigidHero
      );
      return rigidHero;
    }

    private static System.Numerics.Quaternion JbaQuatMultiply(
      System.Numerics.Quaternion a,
      System.Numerics.Quaternion b
    ) {
      // gl-matrix quat.mul(out, a, b), copied component-for-component.  Do not
      // normalize: bind quaternions recovered by mat4.getRotation() can be
      // non-unit when the source matrix contains scale/shear.
      return new System.Numerics.Quaternion(
        a.X * b.W + a.W * b.X + a.Y * b.Z - a.Z * b.Y,
        a.Y * b.W + a.W * b.Y + a.Z * b.X - a.X * b.Z,
        a.Z * b.W + a.W * b.Z + a.X * b.Y - a.Y * b.X,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
      );
    }

    private static System.Numerics.Vector3 JbaTransformQuat(
      System.Numerics.Vector3 v,
      System.Numerics.Quaternion q
    ) {
      // Exact gl-matrix vec3.transformQuat() arithmetic.  In particular this
      // deliberately does not normalize q before transforming the vector.
      Single x = v.X;
      Single y = v.Y;
      Single z = v.Z;
      Single qx = q.X;
      Single qy = q.Y;
      Single qz = q.Z;
      Single qw = q.W;

      Single ix = qw * x + qy * z - qz * y;
      Single iy = qw * y + qz * x - qx * z;
      Single iz = qw * z + qx * y - qy * x;
      Single iw = -qx * x - qy * y - qz * z;

      return new System.Numerics.Vector3(
        ix * qw + iw * -qx + iy * -qz - iz * -qy,
        iy * qw + iw * -qy + iz * -qx - ix * -qz,
        iz * qw + iw * -qz + ix * -qy - iy * -qx
      );
    }

    private static Boolean JbaFinite(System.Numerics.Quaternion q) {
      return Single.IsFinite(q.X)
        && Single.IsFinite(q.Y)
        && Single.IsFinite(q.Z)
        && Single.IsFinite(q.W);
    }

    private static Boolean JbaFinite(System.Numerics.Vector3 v) {
      return Single.IsFinite(v.X)
        && Single.IsFinite(v.Y)
        && Single.IsFinite(v.Z);
    }

    private static System.Numerics.Quaternion JbaSampleQuaternion(
      JBATransform sample
    ) {
      System.Numerics.Quaternion q = sample.Rotation;
      if (!JbaFinite(q) || q.LengthSquared() <= 0.000001F)
        return System.Numerics.Quaternion.Identity;

      // jba-read.js normalizes every decoded key.  SampleInto() also slerps
      // normalized endpoints, so this is normally a no-op but protects the
      // pose path from malformed data exactly where Jedipedia would have a
      // normalized quaternion.
      return System.Numerics.Quaternion.Normalize(q);
    }

    private static Matrix JbaMorphemePoseToHero(
      System.Numerics.Quaternion rotation,
      System.Numerics.Vector3 translation
    ) {
      SlimDX.Quaternion slimRotation = new SlimDX.Quaternion(
        rotation.X,
        rotation.Y,
        rotation.Z,
        rotation.W
      );

      Matrix morpheme = JedipediaRotationMatrix(slimRotation);
      morpheme.M41 = translation.X;
      morpheme.M42 = translation.Y;
      morpheme.M43 = translation.Z;
      morpheme.M44 = 1.0F;

      // Transpose-equivalent of Jedipedia jbaPoseMatrices():
      //   heroCol = B^-1 * morphemeCol * B
      // becomes, for SlimDX row vectors,
      //   heroRow = B^T * morphemeRow * (B^-1)^T.
      Matrix heroToMorpheme = HeroToMorphemeBasis();
      Matrix morphemeToHero = MorphemeToHeroBasis();
      Matrix temp;
      Matrix hero;
      Matrix.Multiply(ref heroToMorpheme, ref morpheme, out temp);
      Matrix.Multiply(ref temp, ref morphemeToHero, out hero);
      return hero;
    }

    private static Boolean TryJbaBindLocalMorpheme(
      Matrix heroBindLocal,
      out System.Numerics.Quaternion rotation,
      out System.Numerics.Vector3 translation
    ) {
      rotation = System.Numerics.Quaternion.Identity;
      translation = System.Numerics.Vector3.Zero;

      Matrix heroToMorpheme = HeroToMorphemeBasis();
      Matrix morphemeToHero = MorphemeToHeroBasis();
      Matrix temp;
      Matrix morpheme;
      Matrix.Multiply(ref morphemeToHero, ref heroBindLocal, out temp);
      Matrix.Multiply(ref temp, ref heroToMorpheme, out morpheme);

      if (!TryJedipediaBindRotation(
            morpheme,
            out SlimDX.Quaternion bindRotation)) {
        return false;
      }

      rotation = new System.Numerics.Quaternion(
        bindRotation.X,
        bindRotation.Y,
        bindRotation.Z,
        bindRotation.W
      );
      translation = new System.Numerics.Vector3(
        morpheme.M41,
        morpheme.M42,
        morpheme.M43
      );

      return JbaFinite(rotation) && JbaFinite(translation);
    }

    private static SlimDX.Quaternion MorphemeRotationToHero(
      System.Numerics.Quaternion q
    ) {
      return new SlimDX.Quaternion(
        q.X,
        q.Z,
        -q.Y,
        q.W
      );
    }

    private static Vector3 MorphemeTranslationToHero(
      System.Numerics.Vector3 p
    ) {
      return new Vector3(
        p.X * 0.001F,
        p.Z * 0.001F,
        -p.Y * 0.001F
      );
    }

    private static Matrix BuildAnimatedLocal(
      Matrix bindLocal,
      JBATransform sample
    ) {
      // Wenn die JBA für diesen Bone keine Translation enthält,
      // behalten wir die Translation der GR2-Bind-Pose.
      Vector3 translation = new Vector3(
    bindLocal.M41,
    bindLocal.M42,
    bindLocal.M43
  );

      if (sample.HasTranslation
          && Single.IsFinite(sample.Translation.X)
          && Single.IsFinite(sample.Translation.Y)
          && Single.IsFinite(sample.Translation.Z)) {

        translation = MorphemeTranslationToHero(sample.Translation);
      }

      System.Numerics.Quaternion nq = sample.Rotation;

      if (!Single.IsFinite(nq.X)
          || !Single.IsFinite(nq.Y)
          || !Single.IsFinite(nq.Z)
          || !Single.IsFinite(nq.W)
          || nq.LengthSquared() <= 0.000001F) {

        return bindLocal;
      }

      nq = System.Numerics.Quaternion.Normalize(nq);

      SlimDX.Quaternion rotation = MorphemeRotationToHero(nq);

      Matrix local = Matrix.RotationQuaternion(rotation);

      local.M41 = translation.X;
      local.M42 = translation.Y;
      local.M43 = translation.Z;
      local.M44 = 1.0F;

      return local;
    }

    private static Vector3 HeroToMorphemeTranslation(Vector3 p) {
      return new Vector3(
        p.X * 1000.0F,
        -p.Z * 1000.0F,
        p.Y * 1000.0F
      );
    }

    private static void BuildChannelBinding(
      JBAAnimation animation,
      JBARig rig,
      IList<GR2_Bone_Skeleton> skeleton,
      out Int32[] skeletonToChannel,
      out Boolean[] useBindTranslation
    ) {
      Int32 skeletonCount = skeleton?.Count ?? 0;
      skeletonToChannel = new Int32[skeletonCount];
      useBindTranslation = new Boolean[skeletonCount];

      for (Int32 i = 0; i < skeletonCount; i++)
        skeletonToChannel[i] = -1;

      if (animation == null || skeleton == null)
        return;

      Int32 sampleCount = Math.Max(0, animation.BoneCount);
      Dictionary<String, Int32> channelByName =
        new Dictionary<String, Int32>(
          StringComparer.OrdinalIgnoreCase
        );

      // Jedipedia treats names already attached to the JBA as authoritative.
      // That includes both creature clips that self-name and names recovered
      // from an AMX sidecar. Once those exist it does NOT layer an MPH mapping
      // on top; doing so can bind one channel to two different skeleton bones
      // when the shared MPH rig and AMX list differ.
      Boolean hasAuthoritativeNames = animation.BoneNames != null
        && animation.BoneNames
          .Take(Math.Min(animation.BoneNames.Count, sampleCount))
          .Any(name => !String.IsNullOrWhiteSpace(name)
            && !name.StartsWith(
                 "bone_",
                 StringComparison.OrdinalIgnoreCase));

      // For unnamed 64-bit clips, recover names from the exact RigToAnimMap.
      if (!hasAuthoritativeNames
          && rig != null
          && rig.Bones != null
          && rig.AnimToRig != null) {

        for (Int32 channel = 0;
             channel < rig.AnimToRig.Length;
             channel++) {

          Int32 rigIndex = rig.AnimToRig[channel];
          if (rigIndex < 0 || rigIndex >= rig.Bones.Count)
            continue;

          String name = CanonicalAnimationBoneName(
            rig.Bones[rigIndex].Name
          );

          if (!String.IsNullOrWhiteSpace(name))
            channelByName[name] = channel;
        }
      }

      if (hasAuthoritativeNames) {
        Int32 namedCount = Math.Min(
          animation.BoneNames.Count,
          sampleCount
        );

        for (Int32 channel = 0; channel < namedCount; channel++) {
          String name = animation.BoneNames[channel];

          if (String.IsNullOrWhiteSpace(name)
              || name.StartsWith(
                   "bone_",
                   StringComparison.OrdinalIgnoreCase)) {
            continue;
          }

          name = CanonicalAnimationBoneName(name);
          channelByName[name] = channel;
        }
      }

      for (Int32 i = 0; i < skeletonCount; i++) {
        String name = CanonicalAnimationBoneName(
          skeleton[i].boneName
        );

        if (channelByName.TryGetValue(name, out Int32 channel)
            && channel >= 0
            && channel < sampleCount) {

          skeletonToChannel[i] = channel;
          useBindTranslation[i] =
            animation.UsesRigBindTranslation(channel);
        }
      }
    }

    private void BuildJbaBindingCache() {
      _jbaPoseSamples = null;
      _jbaSkeletonToChannel = null;
      _jbaUseBindTranslation = null;
      _jbaBoundSkeletonBoneCount = 0;
      _jbaBoundChannelCount = 0;
      _jbaBaseBoundSkeletonBoneCount = 0;
      _jbaBaseBoundChannelCount = 0;
      _jbaBasePoseSamples = null;
      _jbaBaseSkeletonToChannel = null;
      _jbaBaseUseBindTranslation = null;
      _jbaBindLocal = null;
      _jbaBindLocalFull = null;
      _jbaInverseBind = null;
      _jbaCurrentWorld = null;
      _jbaCurrentValid = null;
      _jbaBindRotationMorpheme = null;
      _jbaBindTranslationMorpheme = null;
      _jbaWorldRotationMorpheme = null;
      _jbaWorldTranslationMorpheme = null;
      _jbaSkinMatrices.Clear();

      if (_jbaAnimation == null || _model == null)
        return;

      IList<GR2_Bone_Skeleton> skeleton = _model.skeleton_bones;
      if (skeleton == null || skeleton.Count == 0)
        return;

      _jbaPoseSamples = new JBATransform[
        Math.Max(0, _jbaAnimation.BoneCount)
      ];

      if (_jbaIsAdditive && _jbaBaseAnimation != null) {
        _jbaBasePoseSamples = new JBATransform[
          Math.Max(0, _jbaBaseAnimation.BoneCount)
        ];
      }

      Int32 count = skeleton.Count;
      _jbaBindLocal = new Matrix[count];
      _jbaBindLocalFull = new Matrix[count];
      _jbaInverseBind = new Matrix[count];
      _jbaCurrentWorld = new Matrix[count];
      _jbaCurrentValid = new Boolean[count];
      _jbaBindRotationMorpheme = new System.Numerics.Quaternion[count];
      _jbaBindTranslationMorpheme = new System.Numerics.Vector3[count];
      _jbaWorldRotationMorpheme = new System.Numerics.Quaternion[count];
      _jbaWorldTranslationMorpheme = new System.Numerics.Vector3[count];

      // IMPORTANT: GR2_Bone_Skeleton already inverts BOTH matrices while
      // reading them (FileHelpers.ReadMatrix(..., true)). The on-disk
      // `rootToBone` is the inverse bind matrix, but `bone.root` here is
      // therefore the BIND-WORLD matrix in SlimDX row-vector form.
      //
      // For row vectors:
      //   bindWorld(child) = bindLocal(child) * bindWorld(parent)
      //   bindLocal(child) = bindWorld(child) * inverse(bindWorld(parent))
      //   skin             = inverse(bindWorld) * animatedWorld
      for (Int32 i = 0; i < count; i++) {
        GR2_Bone_Skeleton bone = skeleton[i];

        Matrix bindWorld = bone.root;

        // GR2_Bone_Skeleton now preserves the on-disk rootToBone.  That is
        // already the exact inverse-bind matrix Jedipedia multiplies after the
        // animated world transform, so never obtain it by inverting the
        // already inverted `bone.root` a second time.
        _jbaInverseBind[i] = bone.rootToBoneRaw;

        Int32 parent = bone.parentBoneIndex;
        Matrix bindLocal;

        if (parent >= 0 && parent < i) {
          Matrix parentInverseBind = skeleton[parent].rootToBoneRaw;
          Matrix.Multiply(
            ref bindWorld,
            ref parentInverseBind,
            out bindLocal
          );
        }
        else {
          bindLocal = bindWorld;
        }

        _jbaBindLocalFull[i] = bindLocal;
        _jbaBindLocal[i] = RigidAnimationLocal(bindLocal);

        if (!TryJbaBindLocalMorpheme(
              bindLocal,
              out _jbaBindRotationMorpheme[i],
              out _jbaBindTranslationMorpheme[i])) {
          // A malformed/non-invertible skeleton did not give Jedipedia a
          // recoverable bind local either.  Keep a harmless identity entry;
          // targetValid will still prevent non-finite data reaching the shader.
          _jbaBindRotationMorpheme[i] = System.Numerics.Quaternion.Identity;
          _jbaBindTranslationMorpheme[i] = System.Numerics.Vector3.Zero;
        }
      }

      BuildChannelBinding(
        _jbaAnimation,
        _jbaRig,
        skeleton,
        out _jbaSkeletonToChannel,
        out _jbaUseBindTranslation
      );

      if (_jbaSkeletonToChannel != null) {
        HashSet<Int32> boundChannels = new HashSet<Int32>();
        for (Int32 i = 0; i < _jbaSkeletonToChannel.Length; i++) {
          Int32 channel = _jbaSkeletonToChannel[i];
          if (channel < 0) continue;
          _jbaBoundSkeletonBoneCount++;
          boundChannels.Add(channel);
        }
        _jbaBoundChannelCount = boundChannels.Count;
      }

      if (_jbaIsAdditive && _jbaBaseAnimation != null) {
        BuildChannelBinding(
          _jbaBaseAnimation,
          _jbaBaseRig,
          skeleton,
          out _jbaBaseSkeletonToChannel,
          out _jbaBaseUseBindTranslation
        );

        HashSet<Int32> baseBoundChannels = new HashSet<Int32>();
        for (Int32 i = 0; i < _jbaBaseSkeletonToChannel.Length; i++) {
          Int32 channel = _jbaBaseSkeletonToChannel[i];
          if (channel < 0) continue;
          _jbaBaseBoundSkeletonBoneCount++;
          baseBoundChannels.Add(channel);
        }
        _jbaBaseBoundChannelCount = baseBoundChannels.Count;
      }
      else {
        _jbaBaseSkeletonToChannel = new Int32[count];
        _jbaBaseUseBindTranslation = new Boolean[count];
        for (Int32 i = 0; i < count; i++)
          _jbaBaseSkeletonToChannel[i] = -1;
      }
    }

    private void BuildJbaWorldPose(
      IReadOnlyList<JBATransform> poseFrame,
      Matrix[] targetWorld,
      Boolean[] targetValid
    ) {
      IList<GR2_Bone_Skeleton> skeleton = _model?.skeleton_bones;

      if (poseFrame == null
          || skeleton == null
          || _jbaSkeletonToChannel == null
          || _jbaBindRotationMorpheme == null
          || _jbaBindTranslationMorpheme == null
          || _jbaWorldRotationMorpheme == null
          || _jbaWorldTranslationMorpheme == null
          || targetWorld == null
          || targetValid == null) {
        return;
      }

      Int32 count = Math.Min(
        skeleton.Count,
        Math.Min(targetWorld.Length, targetValid.Length)
      );

      // This is intentionally a literal C# translation of Jedipedia's
      // jbaComposeWorld().  Do the hierarchy arithmetic as Morpheme
      // quaternions/vectors and only create a matrix after the complete world
      // pose is known.  The distinction is essential for bind quaternions
      // recovered from scale/shear matrices: quat.mul() followed by one
      // fromRotationTranslation() is not the same operation as multiplying a
      // matrix per local when those quaternions are not unit length.
      for (Int32 i = 0; i < count; i++) {
        GR2_Bone_Skeleton bone = skeleton[i];
        Int32 channel = _jbaSkeletonToChannel[i];
        Boolean driven = channel >= 0 && channel < poseFrame.Count;

        System.Numerics.Quaternion rotation;
        System.Numerics.Vector3 translation;

        if (driven) {
          JBATransform sample = poseFrame[channel];
          rotation = JbaSampleQuaternion(sample);

          // Jedipedia's jbaRigTranslations() substitutes the target rig bind
          // offset only for constant channels.  Otherwise it uses the decoded
          // translation array verbatim (including TranslationBase on a block
          // that stored no per-frame translation keys).
          translation = _jbaUseBindTranslation[i]
            ? _jbaBindTranslationMorpheme[i]
            : sample.Translation;
        }
        else {
          rotation = _jbaBindRotationMorpheme[i];
          translation = _jbaBindTranslationMorpheme[i];
        }

        if (!JbaFinite(rotation) || !JbaFinite(translation)) {
          targetValid[i] = false;
          _jbaWorldRotationMorpheme[i] = System.Numerics.Quaternion.Identity;
          _jbaWorldTranslationMorpheme[i] = System.Numerics.Vector3.Zero;
          targetWorld[i] = Matrix.Identity;
          continue;
        }

        Int32 parent = bone.parentBoneIndex;
        Boolean parentDriven =
          parent >= 0
          && parent < i
          && _jbaSkeletonToChannel[parent] >= 0;

        if (parent >= 0
            && parent < i
            && targetValid[parent]
            && (!driven || parentDriven)) {

          System.Numerics.Quaternion parentRotation =
            _jbaWorldRotationMorpheme[parent];

          _jbaWorldRotationMorpheme[i] = JbaQuatMultiply(
            parentRotation,
            rotation
          );
          _jbaWorldTranslationMorpheme[i] =
            JbaTransformQuat(translation, parentRotation)
            + _jbaWorldTranslationMorpheme[parent];
        }
        else {
          _jbaWorldRotationMorpheme[i] = rotation;
          _jbaWorldTranslationMorpheme[i] = translation;
        }

        targetWorld[i] = JbaMorphemePoseToHero(
          _jbaWorldRotationMorpheme[i],
          _jbaWorldTranslationMorpheme[i]
        );
        targetValid[i] = true;
      }
    }

    private static Matrix JbaRotationMatrix(JBATransform sample) {
      System.Numerics.Quaternion nq = sample.Rotation;

      if (!Single.IsFinite(nq.X)
          || !Single.IsFinite(nq.Y)
          || !Single.IsFinite(nq.Z)
          || !Single.IsFinite(nq.W)
          || nq.LengthSquared() <= 0.000001F) {
        nq = System.Numerics.Quaternion.Identity;
      }
      else {
        nq = System.Numerics.Quaternion.Normalize(nq);
      }

      return Matrix.RotationQuaternion(
        MorphemeRotationToHero(nq)
      );
    }

    private static Matrix RotationOnly(Matrix matrix) {
      matrix.M41 = 0.0F;
      matrix.M42 = 0.0F;
      matrix.M43 = 0.0F;
      matrix.M44 = 1.0F;
      return matrix;
    }

    private void BuildJbaAdditiveWorldPose(
      IReadOnlyList<JBATransform> overlayFrame,
      IReadOnlyList<JBATransform> baseFrame,
      Matrix[] targetWorld,
      Boolean[] targetValid
    ) {
      IList<GR2_Bone_Skeleton> skeleton = _model?.skeleton_bones;

      if (overlayFrame == null
          || skeleton == null
          || _jbaSkeletonToChannel == null
          || _jbaBaseSkeletonToChannel == null
          || _jbaBindRotationMorpheme == null
          || _jbaBindTranslationMorpheme == null
          || _jbaWorldRotationMorpheme == null
          || _jbaWorldTranslationMorpheme == null
          || targetWorld == null
          || targetValid == null) {
        return;
      }

      Int32 count = Math.Min(
        skeleton.Count,
        Math.Min(targetWorld.Length, targetValid.Length)
      );

      // Literal equivalent of jbaComposeAdditiveLocals() +
      // jbaComposeWorld(): compose the additive LOCAL quaternion on the left,
      // add its local translation, then walk the hierarchy in Morpheme space.
      for (Int32 i = 0; i < count; i++) {
        GR2_Bone_Skeleton bone = skeleton[i];

        Int32 overlayChannel = _jbaSkeletonToChannel[i];
        Boolean overlayDriven =
          overlayChannel >= 0
          && overlayChannel < overlayFrame.Count;

        Int32 baseChannel = _jbaBaseSkeletonToChannel[i];
        Boolean baseDriven =
          baseFrame != null
          && baseChannel >= 0
          && baseChannel < baseFrame.Count;

        System.Numerics.Quaternion baseRotation =
          _jbaBindRotationMorpheme[i];
        System.Numerics.Vector3 baseTranslation =
          _jbaBindTranslationMorpheme[i];

        if (baseDriven) {
          JBATransform baseSample = baseFrame[baseChannel];
          baseRotation = JbaSampleQuaternion(baseSample);
          baseTranslation = _jbaBaseUseBindTranslation[i]
            ? _jbaBindTranslationMorpheme[i]
            : baseSample.Translation;
        }

        System.Numerics.Quaternion overlayRotation =
          System.Numerics.Quaternion.Identity;
        System.Numerics.Vector3 overlayTranslation =
          System.Numerics.Vector3.Zero;

        if (overlayDriven) {
          JBATransform overlaySample = overlayFrame[overlayChannel];
          overlayRotation = JbaSampleQuaternion(overlaySample);
          // Additive translations are deltas.  Never substitute the rig's bind
          // offset here, even when the delta channel is constant zero.
          overlayTranslation = overlaySample.Translation;
        }

        System.Numerics.Quaternion localRotation =
          overlayDriven
            ? JbaQuatMultiply(overlayRotation, baseRotation)
            : baseRotation;
        System.Numerics.Vector3 localTranslation =
          baseTranslation + overlayTranslation;

        if (!JbaFinite(localRotation) || !JbaFinite(localTranslation)) {
          targetValid[i] = false;
          _jbaWorldRotationMorpheme[i] = System.Numerics.Quaternion.Identity;
          _jbaWorldTranslationMorpheme[i] = System.Numerics.Vector3.Zero;
          targetWorld[i] = Matrix.Identity;
          continue;
        }

        Boolean driven = overlayDriven || baseDriven;
        Int32 parent = bone.parentBoneIndex;
        Boolean parentDriven = false;
        if (parent >= 0 && parent < i) {
          Boolean parentOverlayDriven = _jbaSkeletonToChannel[parent] >= 0;
          Boolean parentBaseDriven = _jbaBaseSkeletonToChannel[parent] >= 0;
          parentDriven = parentOverlayDriven || parentBaseDriven;
        }

        if (parent >= 0
            && parent < i
            && targetValid[parent]
            && (!driven || parentDriven)) {

          System.Numerics.Quaternion parentRotation =
            _jbaWorldRotationMorpheme[parent];

          _jbaWorldRotationMorpheme[i] = JbaQuatMultiply(
            parentRotation,
            localRotation
          );
          _jbaWorldTranslationMorpheme[i] =
            JbaTransformQuat(localTranslation, parentRotation)
            + _jbaWorldTranslationMorpheme[parent];
        }
        else {
          _jbaWorldRotationMorpheme[i] = localRotation;
          _jbaWorldTranslationMorpheme[i] = localTranslation;
        }

        targetWorld[i] = JbaMorphemePoseToHero(
          _jbaWorldRotationMorpheme[i],
          _jbaWorldTranslationMorpheme[i]
        );
        targetValid[i] = true;
      }
    }

    private static Boolean IsFinitePoint(Vector3 point) {
      return Single.IsFinite(point.X)
        && Single.IsFinite(point.Y)
        && Single.IsFinite(point.Z);
    }

    private void RebuildAnimationSkeletonDebugGeometry() {
      _jbaBoneVertexCount = 0;

      if (!_showSkeleton
          || Device == null
          || ImmediateContext == null
          || _model == null
          || _jbaCurrentWorld == null
          || _jbaCurrentValid == null) {
        return;
      }

      IList<GR2_Bone_Skeleton> skeleton = _model.skeleton_bones;
      if (skeleton == null || skeleton.Count <= 1)
        return;

      // Two vertices per parent-child link. Allocate for the skeleton once and
      // update it with WRITE_DISCARD as the animation advances. This keeps the
      // debug overlay out of the GC-heavy playback path that caused the first
      // JBA stutter issue.
      Int32 requiredCapacity = Math.Max(2, (skeleton.Count - 1) * 2);

      if (_jbaBoneVertices == null
          || _jbaBoneVertices.Length < requiredCapacity) {
        _jbaBoneVertices = new PosNormalTexTan[requiredCapacity];
      }

      Int32 vertexCount = 0;
      Int32 count = Math.Min(
        skeleton.Count,
        Math.Min(_jbaCurrentWorld.Length, _jbaCurrentValid.Length)
      );

      for (Int32 i = 0; i < count; i++) {
        Int32 parent = skeleton[i].parentBoneIndex;
        if (parent < 0 || parent >= count)
          continue;

        if (!_jbaCurrentValid[i] || !_jbaCurrentValid[parent])
          continue;

        Matrix childWorld = _jbaCurrentWorld[i];
        Matrix parentWorld = _jbaCurrentWorld[parent];

        Vector3 child = new Vector3(
          childWorld.M41,
          childWorld.M42,
          childWorld.M43
        );
        Vector3 parentPos = new Vector3(
          parentWorld.M41,
          parentWorld.M42,
          parentWorld.M43
        );

        if (!IsFinitePoint(child) || !IsFinitePoint(parentPos))
          continue;

        // The generic GR2 shader only needs a valid vertex basis. The skeleton
        // uses the neutral preview material; normals/tangents are irrelevant
        // for line topology but keep the input layout fully initialized.
        _jbaBoneVertices[vertexCount++] = new PosNormalTexTan(
          parentPos,
          Vector3.UnitZ,
          Vector2.Zero,
          Vector3.UnitX
        );
        _jbaBoneVertices[vertexCount++] = new PosNormalTexTan(
          child,
          Vector3.UnitZ,
          Vector2.Zero,
          Vector3.UnitX
        );
      }

      if (vertexCount <= 0)
        return;

      if (_jbaBoneBuffer == null
          || _jbaBoneBufferCapacity < requiredCapacity) {
        Util.ReleaseCom(ref _jbaBoneBuffer);

        BufferDescription vbd = new BufferDescription(
          PosNormalTexTan.Stride * requiredCapacity,
          ResourceUsage.Dynamic,
          BindFlags.VertexBuffer,
          CpuAccessFlags.Write,
          ResourceOptionFlags.None,
          0
        );

        _jbaBoneBuffer = new Buffer(
          Device,
          new DataStream(_jbaBoneVertices, false, false),
          vbd
        );
        _jbaBoneBufferCapacity = requiredCapacity;
      }

      try {
        DataBox mapped = ImmediateContext.MapSubresource(
          _jbaBoneBuffer,
          MapMode.WriteDiscard,
          SlimDX.Direct3D11.MapFlags.None
        );
        mapped.Data.WriteRange(_jbaBoneVertices);
        ImmediateContext.UnmapSubresource(_jbaBoneBuffer, 0);
        _jbaBoneVertexCount = vertexCount;
      }
      catch (Exception ex) {
        _jbaBoneVertexCount = 0;
        System.Diagnostics.Debug.WriteLine(
          "JBA skeleton overlay update failed: " + ex.Message
        );
      }
    }

    private Boolean IsJbaSkeletonBoneDriven(Int32 skeletonIndex) {
      if (skeletonIndex < 0) return false;

      Boolean mainDriven = _jbaSkeletonToChannel != null
        && skeletonIndex < _jbaSkeletonToChannel.Length
        && _jbaSkeletonToChannel[skeletonIndex] >= 0;

      if (mainDriven) return true;

      return _jbaIsAdditive
        && _jbaBaseSkeletonToChannel != null
        && skeletonIndex < _jbaBaseSkeletonToChannel.Length
        && _jbaBaseSkeletonToChannel[skeletonIndex] >= 0;
    }

    private Int32 JbaDrivenAncestor(Int32 skeletonIndex) {
      IList<GR2_Bone_Skeleton> skeleton = _model?.skeleton_bones;
      if (skeleton == null
          || skeletonIndex < 0
          || skeletonIndex >= skeleton.Count)
        return -1;

      Int32 at = skeletonIndex;
      while (at >= 0 && at < skeleton.Count) {
        if (IsJbaSkeletonBoneDriven(at)) return at;

        Int32 parent = skeleton[at].parentBoneIndex;
        if (parent < 0 || parent >= at) break;
        at = parent;
      }

      return -1;
    }

    private void RebuildAnimationSkeletonBuffer() {
      _jbaBoneVertexCount = 0;

      if (_jbaAnimation == null
          || Device == null
          || _model == null) {
        return;
      }

      IList<GR2_Bone_Skeleton> skeleton = _model.skeleton_bones;
      if (skeleton == null || skeleton.Count == 0)
        return;

      if (_jbaPoseSamples == null
          || _jbaSkeletonToChannel == null
          || _jbaBindLocal == null
          || _jbaBindLocalFull == null
          || _jbaInverseBind == null
          || _jbaBindRotationMorpheme == null
          || _jbaBindTranslationMorpheme == null
          || _jbaWorldRotationMorpheme == null
          || _jbaWorldTranslationMorpheme == null
          || _jbaSkeletonToChannel.Length != skeleton.Count) {
        BuildJbaBindingCache();
      }

      if (_jbaPoseSamples == null
          || _jbaCurrentWorld == null
          || _jbaCurrentValid == null
          || _jbaBindLocal == null
          || _jbaInverseBind == null
          || _jbaBindRotationMorpheme == null
          || _jbaBindTranslationMorpheme == null
          || _jbaWorldRotationMorpheme == null
          || _jbaWorldTranslationMorpheme == null) {
        return;
      }

      _jbaFrame = _jbaAnimation.SampleInto(
        _jbaTime,
        _jbaPoseSamples
      );

      if (_jbaIsAdditive) {
        IReadOnlyList<JBATransform> baseFrame = null;

        if (_jbaBaseAnimation != null
            && _jbaBasePoseSamples != null) {
          _jbaBaseAnimation.SampleInto(
            _jbaBaseTime,
            _jbaBasePoseSamples
          );
          baseFrame = _jbaBasePoseSamples;
        }

        BuildJbaAdditiveWorldPose(
          _jbaPoseSamples,
          baseFrame,
          _jbaCurrentWorld,
          _jbaCurrentValid
        );
      }
      else {
        BuildJbaWorldPose(
          _jbaPoseSamples,
          _jbaCurrentWorld,
          _jbaCurrentValid
        );
      }

      _jbaSkinMatrices.Clear();

      Int32 count = skeleton.Count;

      for (Int32 i = 0; i < count; i++) {
        String boneName = CanonicalAnimationBoneName(
          skeleton[i].boneName
        );

        Matrix skin = Matrix.Identity;

        if (_jbaCurrentValid[i]) {
          // Exact transpose-equivalent of Jedipedia jbaPoseMatrices():
          //   skinCol = animatedWorldCol * rootToBoneCol
          // SlimDX/PugTools uses row vectors, therefore:
          //   skinRow = rootToBoneRow * animatedWorldRow
          Matrix inverseBind = _jbaInverseBind[i];
          Matrix current = _jbaCurrentWorld[i];
          Matrix.Multiply(
            ref inverseBind,
            ref current,
            out skin
          );

          Boolean validSkin =
            Single.IsFinite(skin.M11)
            && Single.IsFinite(skin.M12)
            && Single.IsFinite(skin.M13)
            && Single.IsFinite(skin.M14)
            && Single.IsFinite(skin.M21)
            && Single.IsFinite(skin.M22)
            && Single.IsFinite(skin.M23)
            && Single.IsFinite(skin.M24)
            && Single.IsFinite(skin.M31)
            && Single.IsFinite(skin.M32)
            && Single.IsFinite(skin.M33)
            && Single.IsFinite(skin.M34)
            && Single.IsFinite(skin.M41)
            && Single.IsFinite(skin.M42)
            && Single.IsFinite(skin.M43)
            && Single.IsFinite(skin.M44);

          if (!validSkin) {
            Int32 parent = skeleton[i].parentBoneIndex;
            if (parent >= 0 && parent < i) {
              String parentName = CanonicalAnimationBoneName(
                skeleton[parent].boneName
              );
              if (_jbaSkinMatrices.TryGetValue(parentName, out Matrix inherited))
                skin = inherited;
              else
                skin = Matrix.Identity;
            }
            else {
              skin = Matrix.Identity;
            }
          }
        }

        _jbaSkinMatrices[boneName] = skin;
      }

      // GPU path: only the matrix palette changes per redraw. The model's
      // immutable skinned vertex buffers remain untouched.
      RebuildAnimationSkeletonDebugGeometry();
    }

    private void DrawAnimationSkeleton(EffectTechnique activeTech) {
      if (!_showSkeleton
          || _jbaAnimation == null
          || _model == null
          || activeTech == null) return;

      if (_jbaBoneBuffer == null || _jbaBoneVertexCount <= 0) return;

      ImmediateContext.InputAssembler.InputLayout = InputLayouts.PosNormalTexTan;
      ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineList;
      ImmediateContext.InputAssembler.SetVertexBuffers(
        0,
        new VertexBufferBinding(_jbaBoneBuffer, PosNormalTexTan.Stride, 0)
      );

      // Draw bones through the mesh, matching Jedipedia's diagnostic overlay.
      // Restore the normal depth state afterwards so the next frame's model
      // rendering is unaffected.
      ImmediateContext.OutputMerger.DepthStencilState = RenderStates.NoDepthDSS;
      ImmediateContext.Rasterizer.State = RenderStates.TwoSidedRS;

      if (_jbaMaterial != null) {
        _fx.SetDiffuseMap(_jbaMaterial.diffuseSRV);
        _fx.SetGlossMap(_jbaMaterial.glossSRV);
        _fx.SetRotationMap(_jbaMaterial.rotationSRV);
      }

      Matrix mvMatrix = Matrix.Identity;
      Matrix.Multiply(ref mvMatrix, ref _cMatrix, out mvMatrix);
      Matrix.Multiply(ref mvMatrix, ref _pMatrix, out Matrix wvp);
      Matrix.Invert(ref mvMatrix, out mvMatrix);
      Matrix.Transpose(ref mvMatrix, out mvMatrix);
      _fx.SetWorldMatrix(mvMatrix);
      _fx.SetMvMatrix(wvp);

      _fx.Generic.GetPassByIndex(0).Apply(ImmediateContext);
      ImmediateContext.Draw(_jbaBoneVertexCount, 0);

      ImmediateContext.OutputMerger.DepthStencilState = RenderStates.LessEqualDSS;
      ImmediateContext.Rasterizer.State = RenderStates.OneSidedRS;
      ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
    }

    private static Int32 NextPowerOfTwo(Int32 value) {
      Int32 result = 1;
      while (result < value && result < 4096)
        result <<= 1;
      return Math.Min(4096, Math.Max(1, result));
    }

    private void BuildAnimationSkeletonLabelAtlas() {
      Util.ReleaseCom(ref _jbaLabelBuffer);
      Util.ReleaseCom(ref _jbaLabelTexture);
      _jbaLabelVertices = null;
      _jbaLabelUvRects = null;
      _jbaLabelPixelSizes = null;
      _jbaLabelBufferCapacity = 0;
      _jbaLabelVertexCount = 0;

      IList<GR2_Bone_Skeleton> skeleton = _model?.skeleton_bones;
      if (Device == null || skeleton == null || skeleton.Count == 0)
        return;

      try {
        Int32 count = skeleton.Count;
        _jbaLabelUvRects = new Vector4[count];
        _jbaLabelPixelSizes = new Vector2[count];

        const Int32 atlasWidth = 2048;
        const Int32 paddingX = 6;
        const Int32 paddingY = 4;

        var rects = new Rectangle[count];
        var labels = new String[count];
        Int32 x = 0;
        Int32 y = 0;
        Int32 rowHeight = 0;

        using (var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(measureBitmap))
        using (var font = new Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point)) {
          graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

          for (Int32 i = 0; i < count; i++) {
            String label = CanonicalAnimationBoneName(skeleton[i].boneName);
            if (String.IsNullOrWhiteSpace(label)) label = "Bone " + i;
            labels[i] = label;

            SizeF measured = graphics.MeasureString(
              label,
              font,
              Int32.MaxValue,
              StringFormat.GenericTypographic
            );
            Int32 width = Math.Max(2, (Int32)Math.Ceiling(measured.Width) + paddingX);
            Int32 height = Math.Max(2, (Int32)Math.Ceiling(measured.Height) + paddingY);

            if (x > 0 && x + width > atlasWidth) {
              x = 0;
              y += rowHeight;
              rowHeight = 0;
            }

            rects[i] = new Rectangle(x, y, Math.Min(width, atlasWidth), height);
            x += width;
            rowHeight = Math.Max(rowHeight, height);
          }
        }

        Int32 usedHeight = y + Math.Max(1, rowHeight);
        Int32 atlasHeight = NextPowerOfTwo(usedHeight);
        if (usedHeight > atlasHeight) {
          System.Diagnostics.Debug.WriteLine(
            "JBA label atlas too tall; labels beyond " + atlasHeight + " px will be skipped."
          );
        }

        using (var atlas = new Bitmap(atlasWidth, atlasHeight, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(atlas))
        using (var font = new Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(230, 0, 0, 0)))
        using (var textBrush = new SolidBrush(Color.FromArgb(255, 255, 205, 30))) {
          graphics.Clear(Color.Transparent);
          graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

          for (Int32 i = 0; i < count; i++) {
            Rectangle rect = rects[i];
            if (rect.Bottom > atlasHeight || rect.Width <= 0 || rect.Height <= 0)
              continue;

            Single tx = rect.X + 2.0F;
            Single ty = rect.Y + 1.0F;
            graphics.DrawString(labels[i], font, shadowBrush, tx + 1.0F, ty + 1.0F, StringFormat.GenericTypographic);
            graphics.DrawString(labels[i], font, textBrush, tx, ty, StringFormat.GenericTypographic);

            _jbaLabelUvRects[i] = new Vector4(
              (Single)rect.Left / atlasWidth,
              (Single)rect.Top / atlasHeight,
              (Single)rect.Right / atlasWidth,
              (Single)rect.Bottom / atlasHeight
            );
            _jbaLabelPixelSizes[i] = new Vector2(rect.Width, rect.Height);
          }

          using (var stream = new MemoryStream()) {
            atlas.Save(stream, ImageFormat.Png);
            _jbaLabelTexture = ShaderResourceView.FromMemory(Device, stream.ToArray());
          }
        }

        _jbaLabelBufferCapacity = Math.Max(6, count * 6);
        _jbaLabelVertices = new PosNormalTexTan[_jbaLabelBufferCapacity];
        var description = new BufferDescription(
          PosNormalTexTan.Stride * _jbaLabelBufferCapacity,
          ResourceUsage.Dynamic,
          BindFlags.VertexBuffer,
          CpuAccessFlags.Write,
          ResourceOptionFlags.None,
          0
        );
        _jbaLabelBuffer = new Buffer(
          Device,
          new DataStream(_jbaLabelVertices, false, false),
          description
        );
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine(
          "ViewGR2: skeleton label atlas init failed: " + ex
        );
        Util.ReleaseCom(ref _jbaLabelBuffer);
        Util.ReleaseCom(ref _jbaLabelTexture);
        _jbaLabelVertices = null;
        _jbaLabelUvRects = null;
        _jbaLabelPixelSizes = null;
        _jbaLabelBufferCapacity = 0;
      }
    }

    private void DrawAnimationSkeletonLabels() {
      if (!_showSkeleton
          || _jbaAnimation == null
          || _model?.skeleton_bones == null
          || _jbaCurrentWorld == null
          || _jbaCurrentValid == null
          || _jbaLabelTexture == null
          || _jbaLabelBuffer == null
          || _jbaLabelVertices == null
          || _jbaLabelUvRects == null
          || _jbaLabelPixelSizes == null
          || ClientWidth <= 0
          || ClientHeight <= 0) {
        return;
      }

      IList<GR2_Bone_Skeleton> skeleton = _model.skeleton_bones;
      Int32 count = Math.Min(
        skeleton.Count,
        Math.Min(_jbaCurrentWorld.Length, _jbaCurrentValid.Length)
      );
      count = Math.Min(count, Math.Min(_jbaLabelUvRects.Length, _jbaLabelPixelSizes.Length));
      if (count <= 0) return;

      Matrix viewProjection;
      Matrix.Multiply(ref _cMatrix, ref _pMatrix, out viewProjection);

      Int32 vertexCount = 0;
      for (Int32 i = 0; i < count; i++) {
        if (!_jbaCurrentValid[i]) continue;
        if (vertexCount + 6 > _jbaLabelVertices.Length) break;

        Matrix world = _jbaCurrentWorld[i];
        Vector3 bone = new Vector3(world.M41, world.M42, world.M43);
        if (!IsFinitePoint(bone)) continue;

        Vector3 screen = Vector3.Project(
          bone,
          0.0F,
          0.0F,
          ClientWidth,
          ClientHeight,
          0.0F,
          1.0F,
          viewProjection
        );

        if (!IsFinitePoint(screen)
            || screen.Z < 0.0F
            || screen.Z > 1.0F
            || screen.X < -220.0F
            || screen.X > ClientWidth + 20.0F
            || screen.Y < -30.0F
            || screen.Y > ClientHeight + 30.0F) {
          continue;
        }

        Vector2 size = _jbaLabelPixelSizes[i];
        if (size.X <= 0.0F || size.Y <= 0.0F) continue;
        Vector4 uv = _jbaLabelUvRects[i];

        Single pxLeft = screen.X + 3.0F;
        Single pxTop = screen.Y - (size.Y * 0.5F);
        Single pxRight = pxLeft + size.X;
        Single pxBottom = pxTop + size.Y;

        Single left = (pxLeft / ClientWidth) * 2.0F - 1.0F;
        Single right = (pxRight / ClientWidth) * 2.0F - 1.0F;
        Single top = 1.0F - (pxTop / ClientHeight) * 2.0F;
        Single bottom = 1.0F - (pxBottom / ClientHeight) * 2.0F;

        Vector3 normal = Vector3.UnitZ;
        Vector3 tangent = Vector3.UnitX;
        _jbaLabelVertices[vertexCount++] = new PosNormalTexTan(new Vector3(left, top, 0.0F), normal, new Vector2(uv.X, uv.Y), tangent);
        _jbaLabelVertices[vertexCount++] = new PosNormalTexTan(new Vector3(right, top, 0.0F), normal, new Vector2(uv.Z, uv.Y), tangent);
        _jbaLabelVertices[vertexCount++] = new PosNormalTexTan(new Vector3(right, bottom, 0.0F), normal, new Vector2(uv.Z, uv.W), tangent);
        _jbaLabelVertices[vertexCount++] = new PosNormalTexTan(new Vector3(left, top, 0.0F), normal, new Vector2(uv.X, uv.Y), tangent);
        _jbaLabelVertices[vertexCount++] = new PosNormalTexTan(new Vector3(right, bottom, 0.0F), normal, new Vector2(uv.Z, uv.W), tangent);
        _jbaLabelVertices[vertexCount++] = new PosNormalTexTan(new Vector3(left, bottom, 0.0F), normal, new Vector2(uv.X, uv.W), tangent);
      }

      _jbaLabelVertexCount = vertexCount;
      if (vertexCount <= 0) return;

      try {
        DataBox mapped = ImmediateContext.MapSubresource(
          _jbaLabelBuffer,
          MapMode.WriteDiscard,
          SlimDX.Direct3D11.MapFlags.None
        );
        mapped.Data.WriteRange(_jbaLabelVertices);
        ImmediateContext.UnmapSubresource(_jbaLabelBuffer, 0);

        ImmediateContext.InputAssembler.InputLayout = InputLayouts.PosNormalTexTan;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ImmediateContext.InputAssembler.SetVertexBuffers(
          0,
          new VertexBufferBinding(_jbaLabelBuffer, PosNormalTexTan.Stride, 0)
        );

        ImmediateContext.OutputMerger.DepthStencilState = RenderStates.NoDepthDSS;
        ImmediateContext.OutputMerger.BlendState = RenderStates.TransparentBS;
        ImmediateContext.Rasterizer.State = RenderStates.TwoSidedRS;

        _fx.SetDiffuseMap(_jbaLabelTexture);
        Matrix identity = Matrix.Identity;
        _fx.SetWorldMatrix(identity);
        _fx.SetMvMatrix(identity);
        _fx.filterDiffuseMap.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.Draw(vertexCount, 0);
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine(
          "JBA skeleton label draw failed: " + ex.Message
        );
      }
      finally {
        ImmediateContext.OutputMerger.DepthStencilState = RenderStates.LessEqualDSS;
        ImmediateContext.OutputMerger.BlendState = RenderStates.AlphaToCoverageBS;
        ImmediateContext.Rasterizer.State = RenderStates.OneSidedRS;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
      }
    }

    internal void MakeScreenshot(ImageFileFormat format) {
      try {
        String filename = Tools.PrepExtractPath(
          _model.filename
          + '-'
          + DateTime.Now.ToString("yyyyMMddHHmmss")
          + '.'
          + format.ToString().ToLower()
        );

        Texture2DDescription outputDesc = new Texture2DDescription {
          Width = ClientWidth,
          Height = ClientHeight,
          MipLevels = 1,
          ArraySize = 1,
          Format = Format.R8G8B8A8_UNorm,
          SampleDescription = new SampleDescription(1, 0),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.None,
          CpuAccessFlags = CpuAccessFlags.None,
        };
        Texture2D outputFile = new Texture2D(Device, outputDesc);
        Texture2D BackBuffer = SlimDX.Direct3D11.Resource.FromSwapChain<Texture2D>(SwapChain, 0);

        ImmediateContext.ResolveSubresource(BackBuffer, 0, outputFile, 0, Format.R8G8B8A8_UNorm);
        Texture2D.ToFile(ImmediateContext, outputFile, format, filename);
        Util.ReleaseCom(ref outputFile);
        ((AssetBrowser)Window).StatusLabel1Text("Screenshot Completed");
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine(ex.ToString());
      }
    }
    protected override void OnMouseDown(Object sender, MouseEventArgs mEvnt) {
      _lastMousePos = mEvnt.Location;
      Window.Controls.Find(RenderPanelName, true).First().Capture = true;
    }
    protected override void OnMouseMove(Object sender, MouseEventArgs mEvnt) {
      if (mEvnt.Button == MouseButtons.Left) {
        Single yDelta = MathF.ToRadians(0.4F * (mEvnt.Y - _lastMousePos.Y));
        Single xDelta = -MathF.ToRadians(0.4F * (mEvnt.X - _lastMousePos.X));

        if (Util.IsKeyDown(Keys.LShiftKey)) {
          xDelta = MathF.ToRadians(0.05F * (mEvnt.X - _lastMousePos.X));
          yDelta = MathF.ToRadians(0.05F * (mEvnt.Y - _lastMousePos.Y));

          _camera.Strafe(-xDelta * _camera.Radius);
          _camera.Fly(yDelta * _camera.Radius);

        } else {
          _camera.Pitch(yDelta);
          _camera.Yaw(-xDelta);
        }
      } else if (mEvnt.Button == MouseButtons.Right) {
        Single xDelta = MathF.ToRadians(0.05F * (mEvnt.X - _lastMousePos.X));
        Single yDelta = MathF.ToRadians(0.05F * (mEvnt.Y - _lastMousePos.Y));

        _camera.Strafe(-xDelta * _camera.Radius);
        _camera.Fly(yDelta * _camera.Radius);
      }

      _lastMousePos = mEvnt.Location;
    }
    protected override void OnMouseUp(Object sender, MouseEventArgs mEvnt) {
      Window.Controls.Find(RenderPanelName, true).First().Capture = false;
    }
    internal void ZoomByWheelDelta(Int32 delta) {
      Single notches = delta / 120.0F;
      if (notches == 0.0F) return;

      Single fraction = Util.IsKeyDown(Keys.ShiftKey) ? 0.025F : 0.12F;
      _cameraZoomSpeed = Math.Max(_camera.Radius * fraction, 0.01F);
      _camera.Zoom(-notches * _cameraZoomSpeed);
    }

    protected override void OnMouseWheel(Object sender, MouseEventArgs mEvnt) {
      ZoomByWheelDelta(mEvnt.Delta);
    }
    public override void OnResize() {
      base.OnResize();
      _camera.SetLens(0.25F * MathF.PI, AspectRatio, 0.001F, 1000.0F);
    }
    public override void UpdateScene(Single dt) {
      base.UpdateScene(dt);

      if (Form.ActiveForm != null) {
        if (Util.IsKeyDown(Keys.R)) {
          _camera.Reset();
          _camera.Position = _cameraPos;
          _camera.LookAt(_cameraPos, _globalBoxCenter, Vector3.UnitY);
        }

        if (Util.IsKeyDown(Keys.Oemplus)) {
          // _flyingCameraSpeed = 15.0F;
          _cameraZoomSpeed = 0.20F;
        }

        if (Util.IsKeyDown(Keys.OemMinus)) {
          // _flyingCameraSpeed = 2.5F;
          _cameraZoomSpeed = 0.05F;
        }

        // if (Util.IsKeyDown(Keys.PageUp)) _camera.Zoom(-_flyingCameraSpeed * dt);

        // if (Util.IsKeyDown(Keys.PageDown)) _camera.Zoom(_flyingCameraSpeed * dt);
      }

      UpdateJbaPlaybackTime();

      // Present(1) already yields to the display refresh while JBA playback is
      // active. The extra sleep could make a near-budget frame miss the next
      // vblank and turn a small workload spike into a visible 33 ms hitch.
      if (!_jbaPlaying)
        System.Threading.Thread.Sleep(1);
    }
  }
}
