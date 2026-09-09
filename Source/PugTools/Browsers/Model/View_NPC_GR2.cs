using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using FileFormats;

using GomLib;

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
  internal class View_NPC_GR2 : D3DPanelApp {
    private EffectTechnique _activeTech;
    private Boolean _disposed;
    private readonly LookAtCamera _camera;
    private Vector3 _cameraPos;
    private Single _cameraZoomSpeed; // = 0.05f;
    private Matrix _cMatrix;
    private GR2 _focus; // = new GR2();
    private String _fqn;
    private GR2_Effect _fx;
    private Vector3 _globalBoxCenter;
    private Vector3 _globalBoxMax;
    private Vector3 _globalBoxMin;
    private Point _lastMousePos;
    private Boolean _makeScreenshot; // = false;
    private Dictionary<String, GR2> _models;
    private Matrix _pMatrix;
    private Dictionary<String, Object> _resources;
    private List<PosNormalTexTan> _vertices; // = new List<PosNormalTexTan>();

    public View_NPC_GR2(IntPtr hInstance,
                        Form form,
                        String panelName = "") : base(hInstance, panelName) {
      Window = form;
      RenderPanelName = panelName;
      Enable4XMsaa = true;
      ClientHeight = form.Controls.Find(panelName, true).First().Height;
      ClientWidth = form.Controls.Find(panelName, true).First().Width;

      _camera = new LookAtCamera();
      _lastMousePos = new Point();
    }

    public override Boolean Init() {
      if (!base.Init()) return false;

      Effects.InitAll(Device);
      _fx = Effects.GR2_FX;
      InputLayouts.InitAll(Device);
      RenderStates.InitAll(Device);

      return true;
    }

    private void BuildGeometry() {
      foreach (KeyValuePair<String, GR2> model in _models) {
        if (model.Value.meshes != null)
          foreach (GR2_Mesh mesh in model.Value.meshes) {
            _vertices = new List<PosNormalTexTan>();

            if (mesh.meshName.Contains("collision")) continue;

            foreach (GR2_Mesh_Vertex vertex in mesh.meshVerts) {
              Vector3 pos = new Vector3(vertex.X, vertex.Y, vertex.Z);
              Vector3 nor = new Vector3(vertex.normX, vertex.normY, vertex.normZ);
              Vector2 tex = new Vector2(vertex.texU, vertex.texV);
              Vector3 tan = new Vector3(vertex.tanX, vertex.tanY, vertex.tanZ);
              _vertices.Add(new PosNormalTexTan(pos, nor, tex, tan));
            }

            BufferDescription vbd = new BufferDescription(
            PosNormalTexTan.Stride * _vertices.Count,
            ResourceUsage.Immutable,
            BindFlags.VertexBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
          );
            mesh.vertBuffer = new Buffer(
              Device,
              new DataStream(_vertices.ToArray(), false, false),
              vbd
            );
            UInt16[] indexArray =
              mesh.meshVertIndex.Select(GR2_Mesh_Vertex_Index => GR2_Mesh_Vertex_Index.index)
                                .ToArray();
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

        if (model.Value.attachedModels.Count > 0)
          foreach (GR2 attachModel in model.Value.attachedModels)
            foreach (GR2_Mesh attachMesh in attachModel.meshes) {
              _vertices = new List<PosNormalTexTan>();

              if (attachMesh.meshName.Contains("collision")) continue;

              foreach (GR2_Mesh_Vertex vertex in attachMesh.meshVerts) {
                Vector3 pos = new Vector3(vertex.X, vertex.Y, vertex.Z);
                Vector3 norm = new Vector3(vertex.normX, vertex.normY, vertex.normZ);
                Vector2 texC = new Vector2(vertex.texU, vertex.texV);
                Vector3 tan = new Vector3(vertex.tanX, vertex.tanY, vertex.tanZ);
                _vertices.Add(new PosNormalTexTan(pos, norm, texC, tan));
              }

              BufferDescription vbd = new BufferDescription(
                PosNormalTexTan.Stride * _vertices.Count,
                ResourceUsage.Immutable,
                BindFlags.VertexBuffer,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                0
              );
              attachMesh.vertBuffer = new Buffer(
                Device,
                new DataStream(_vertices.ToArray(), false, false),
                vbd
              );
              UInt16[] indexArray = attachMesh.meshVertIndex.Select(
                GR2_Mesh_Vertex_Index => GR2_Mesh_Vertex_Index.index
              ).ToArray();
              BufferDescription ibd = new BufferDescription(
                sizeof(ushort) * indexArray.Length,
                ResourceUsage.Immutable,
                BindFlags.IndexBuffer,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                0
              );
              attachMesh.idxBuffer =
                new Buffer(Device, new DataStream(indexArray, false, false), ibd);
            }
      }
    }

    public void Clear() {
      if (_models != null) {
        foreach (KeyValuePair<String, GR2> model in _models) {
          if (model.Value.meshes != null)
            foreach (GR2_Mesh mesh in model.Value.meshes) {
              Util.ReleaseCom(ref mesh.idxBuffer);
              Util.ReleaseCom(ref mesh.vertBuffer);
            }

          if (model.Value.materials != null)
            foreach (GR2_Material mat in model.Value.materials) {
              Util.ReleaseCom(ref mat.diffuseSRV);
              Util.ReleaseCom(ref mat.rotationSRV);
              Util.ReleaseCom(ref mat.glossSRV);
              Util.ReleaseCom(ref mat.paletteSRV);
              Util.ReleaseCom(ref mat.paletteMaskSRV);
              Util.ReleaseCom(ref mat.complexionSRV);
              Util.ReleaseCom(ref mat.facepaintSRV);
              Util.ReleaseCom(ref mat.ageSRV);
            }

          if (model.Value.attachedModels?.Count > 0) {
            foreach (GR2 attach in model.Value.attachedModels) {
              if (attach.meshes != null)
                foreach (GR2_Mesh mesh in attach.meshes) {
                  Util.ReleaseCom(ref mesh.idxBuffer);
                  Util.ReleaseCom(ref mesh.vertBuffer);
                }

              if (attach.materials != null)
                foreach (GR2_Material mat in attach.materials) {
                  Util.ReleaseCom(ref mat.diffuseSRV);
                  Util.ReleaseCom(ref mat.rotationSRV);
                  Util.ReleaseCom(ref mat.glossSRV);
                  Util.ReleaseCom(ref mat.paletteSRV);
                  Util.ReleaseCom(ref mat.paletteMaskSRV);
                  Util.ReleaseCom(ref mat.complexionSRV);
                  Util.ReleaseCom(ref mat.facepaintSRV);
                  Util.ReleaseCom(ref mat.ageSRV);
                }

              attach.Dispose();
            }
          }

          model.Value.Dispose();
        }

        _models.Clear();
      }

      // Clear() can be called while the render panel is only partially initialized
      // (for example after a failed/empty MNT/GR2 preview).
      _resources?.Clear();
      _vertices?.Clear();
      // indexes.Clear();
      // indexList.Clear();
    }

    protected override void Dispose(Boolean disposing) {
      if (!_disposed) {
        if (disposing) {
          Window = null;

          foreach (KeyValuePair<String, GR2> model in _models) {
            foreach (GR2_Mesh mesh in model.Value.meshes) {
              Util.ReleaseCom(ref mesh.idxBuffer);
              Util.ReleaseCom(ref mesh.vertBuffer);
            }

            foreach (GR2_Material mat in model.Value.materials) {
              Util.ReleaseCom(ref mat.diffuseSRV);
              Util.ReleaseCom(ref mat.rotationSRV);
              Util.ReleaseCom(ref mat.glossSRV);
              Util.ReleaseCom(ref mat.paletteSRV);
              Util.ReleaseCom(ref mat.paletteMaskSRV);
              Util.ReleaseCom(ref mat.complexionSRV);
              Util.ReleaseCom(ref mat.facepaintSRV);
              Util.ReleaseCom(ref mat.ageSRV);
            }

            if (model.Value.attachedModels.Count > 0) {
              foreach (GR2 attach in model.Value.attachedModels) {
                foreach (GR2_Mesh mesh in attach.meshes) {
                  Util.ReleaseCom(ref mesh.idxBuffer);
                  Util.ReleaseCom(ref mesh.vertBuffer);
                }

                foreach (GR2_Material mat in attach.materials) {
                  Util.ReleaseCom(ref mat.diffuseSRV);
                  Util.ReleaseCom(ref mat.rotationSRV);
                  Util.ReleaseCom(ref mat.glossSRV);
                  Util.ReleaseCom(ref mat.paletteSRV);
                  Util.ReleaseCom(ref mat.paletteMaskSRV);
                  Util.ReleaseCom(ref mat.complexionSRV);
                  Util.ReleaseCom(ref mat.facepaintSRV);
                  Util.ReleaseCom(ref mat.ageSRV);
                }

                attach.Dispose();
              }
            }

            model.Value.Dispose();
          }

          Effects.DestroyAll();
          InputLayouts.DestroyAll();
          RenderStates.DestroyAll();
        }

        _disposed = true;
      }

      base.Dispose(disposing);
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
      // ImmediateContext.OutputMerger.BlendState = RenderStates.AlphaToCoverageBS;
      ImmediateContext.OutputMerger.BlendState = RenderStates.TransparentBS;
      ImmediateContext.Rasterizer.State = RenderStates.OneSidedRS;

      _camera.UpdateViewMatrix();
      _cMatrix = _camera.View;
      _pMatrix = _camera.Proj;

      // The render thread can start one frame before the effect system has
      // finished initializing (notably for MNT/vehicle previews). Do not let
      // that race terminate the process.
      if (_fx == null || _fx.Generic == null || _models == null) {
        SwapChain.Present(1, PresentFlags.None);
        return;
      }

      _activeTech = _fx.Generic;

      if (Form.ActiveForm != null) {
        if (Util.IsKeyDown(Keys.Q)) _activeTech = _fx.filterPaletteMask;
        if (Util.IsKeyDown(Keys.E)) _activeTech = _fx.filterDiffuseMap;
        if (Util.IsKeyDown(Keys.D1)) _activeTech = _fx.filterPalette1;
        if (Util.IsKeyDown(Keys.D2)) _activeTech = _fx.filterPalette2;
        if (Util.IsKeyDown(Keys.D3)) _activeTech = _fx.filterPaletteMap;
        if (Util.IsKeyDown(Keys.D4)) _activeTech = _fx.filterComplexionMap;
        if (Util.IsKeyDown(Keys.D5)) _activeTech = _fx.filterFacepaintMap;
        if (Util.IsKeyDown(Keys.D6)) _activeTech = _fx.filterAgeMap;
        if (Util.IsKeyDown(Keys.C))
          ImmediateContext.Rasterizer.State = RenderStates.WireframeNoneRS;
        if (Util.IsKeyDown(Keys.PrintScreen)) _makeScreenshot = true;
      }

      foreach (KeyValuePair<String, GR2> model in _models) {
        if (!model.Value.enabled) continue;

        Matrix mvMatrix = model.Value.GetTransform();
        Matrix.Multiply(ref mvMatrix, ref _cMatrix, out mvMatrix);
        Matrix.Multiply(ref mvMatrix, ref _pMatrix, out Matrix wvp);
        Matrix.Invert(ref mvMatrix, out mvMatrix);
        Matrix.Transpose(ref mvMatrix, out mvMatrix);

        _fx.SetWorldMatrix(mvMatrix);
        _fx.SetMvMatrix(wvp);

        if (model.Value.meshes != null)
          foreach (GR2_Mesh mesh in model.Value.meshes) {
            if (mesh.meshName.Contains("collision")) continue;

            Int32 pieceCount = 0;

            foreach (GR2_Mesh_Piece piece in mesh.meshPieces) {
              ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(
                mesh.vertBuffer,
                PosNormalTexTan.Stride,
                0
                )
              );
              ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer, Format.R16_UInt, 0);

              if (model.Value.filename.Contains("head_")) {
                if (model.Value.materials.Count > 0)
                  if (model.Value.materials.Count == 2 && pieceCount > 0)
                    SetMaterial(model.Value.materials[1]);
                  else
                    SetMaterial(model.Value.materials[0]);
              } else {
                if (piece.matId != -1) {
                  if (model.Value.materials.ElementAtOrDefault(piece.matId) != null)
                    SetMaterial(model.Value.materials[piece.matId]);
                } else if (model.Value.materials.Count > 0) {
                  GR2_Material selectedMaterial;

                  if (model.Value.materials.Count == 2 && pieceCount > 0)
                    selectedMaterial = model.Value.materials[1];
                  else
                    selectedMaterial = model.Value.materials[0];

                  SetMaterial(selectedMaterial);
                }
              }

              _activeTech.GetPassByIndex(0).Apply(ImmediateContext);
              ImmediateContext.DrawIndexed(
                ((Int32)piece.numPieceFaces) * 3,
                ((Int32)piece.startIndex) * 3,
                0
              );

              pieceCount++;
            }
          }

        if (model.Value.attachedModels.Count > 0)
          foreach (GR2 attachModel in model.Value.attachedModels) {
            if (!attachModel.enabled) continue;

            foreach (GR2_Mesh attachMesh in attachModel.meshes) {
              if (attachMesh.meshName.Contains("collision")) continue;

              foreach (GR2_Mesh_Piece attachPiece in attachMesh.meshPieces) {
                ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(
                  attachMesh.vertBuffer,
                  PosNormalTexTan.Stride,
                  0
                  )
                );
                ImmediateContext.InputAssembler.SetIndexBuffer(
                  attachMesh.idxBuffer,
                  Format.R16_UInt,
                  0
                );

                if (attachPiece.matId != -1)
                  SetMaterial(attachModel.materials[attachPiece.matId]);
                else
                  SetMaterial(attachModel.materials[0]);

                _activeTech.GetPassByIndex(0).Apply(ImmediateContext);
                ImmediateContext.DrawIndexed(
                  ((Int32)attachPiece.numPieceFaces) * 3,
                  ((Int32)attachPiece.startIndex) * 3,
                  0
                );
              }
            }
          }
      }

      SwapChain.Present(1, PresentFlags.None);

      if (_makeScreenshot) {
        MakeScreenshot(ImageFileFormat.Png);
        _makeScreenshot = false;
      }
    }

    public override Boolean Equals(Object obj) {
      return obj is View_NPC_GR2 gr && _pMatrix.Equals(gr._pMatrix);
    }

    public override Int32 GetHashCode() => base.GetHashCode();

    private static Boolean IsFinite(Single value) {
      return !Single.IsNaN(value) && !Single.IsInfinity(value);
    }

    private static void ExpandTransformedBounds(
      GR2 model,
      ref Vector3 boundsMin,
      ref Vector3 boundsMax
    ) {
      if (model == null || model.globalBox == null) return;

      Matrix transform = model.GetTransform();
      Single[] xs = { model.globalBox.minX, model.globalBox.maxX };
      Single[] ys = { model.globalBox.minY, model.globalBox.maxY };
      Single[] zs = { model.globalBox.minZ, model.globalBox.maxZ };

      foreach (Single x in xs)
        foreach (Single y in ys)
          foreach (Single z in zs) {
            Vector4 corner = Vector3.Transform(new Vector3(x, y, z), transform);
            if (!IsFinite(corner.X) || !IsFinite(corner.Y) || !IsFinite(corner.Z))
              continue;

            boundsMin.X = Math.Min(boundsMin.X, corner.X);
            boundsMin.Y = Math.Min(boundsMin.Y, corner.Y);
            boundsMin.Z = Math.Min(boundsMin.Z, corner.Z);
            boundsMax.X = Math.Max(boundsMax.X, corner.X);
            boundsMax.Y = Math.Max(boundsMax.Y, corner.Y);
            boundsMax.Z = Math.Max(boundsMax.Z, corner.Z);
          }
    }

    public void LoadModel(Dictionary<String, GR2> models,
                          Dictionary<String, Object> resources,
                          String fqn,
                          String type = "") {
      _fqn = fqn;

      Single fac;

      switch (type) {
        case "dyn":
          fac = 2.25f;
          break;
        case "itm":
          fac = 1.75f;
          break;
        case "mnt":
          fac = 2.5f;
          break;
        case "nppTypeHumanoid":
          fac = 1.4f;
          break;
        case "nppTypeCreature":
          fac = 1.4f;
          break;
        default:
          fac = 2.0f;
          break;
      }

      _globalBoxCenter = new Vector3();
      _globalBoxMax = new Vector3(Single.MinValue, Single.MinValue, Single.MinValue);
      _globalBoxMin = new Vector3(Single.MaxValue, Single.MaxValue, Single.MaxValue);

      if (type == "ipp") {
        // A historical IPP can legitimately miss its appearance index when the matching
        // archive is not selected. Never let an empty model dictionary terminate the UI.
        if (models == null || models.Count == 0) {
          _focus = null;
          _globalBoxMin = new Vector3(-1f, -1f, -1f);
          _globalBoxMax = new Vector3(1f, 1f, 1f);
          _globalBoxCenter = Vector3.Zero;
          _cameraPos = new Vector3(2f, 1.3f, 2f);
          return;
        }
        _focus = models.First().Value;

        ExpandTransformedBounds(_focus, ref _globalBoxMin, ref _globalBoxMax);

        _globalBoxCenter = _globalBoxMin + (_globalBoxMax - _globalBoxMin) / 2;
        _cameraPos = _globalBoxCenter + new Vector3(1.0F, 0.65F, 1.0F) *
          Math.Max((_globalBoxMax - _globalBoxMin).Length() * 1.75F, 2.0F);

      } else {
        // Vehicle/glider FxSpecs can contain helper meshes (for example
        // speeder_bike) that are intentionally positioned a long way from the
        // visible vehicle.  Including those helpers in the camera bounds makes
        // the actual mount appear as a tiny dot.  For MNT previews prefer the
        // real veh_* GR2(s) for camera framing, while still rendering every
        // model normally.
        IEnumerable<KeyValuePair<String, GR2>> cameraModels = models;

        if (type == "mnt") {
          List<KeyValuePair<String, GR2>> vehicleModels =
            models.Where(x =>
              !x.Key.Contains("skeleton", StringComparison.OrdinalIgnoreCase)
              && (
                x.Key.Contains("veh_", StringComparison.OrdinalIgnoreCase)
                || (!String.IsNullOrWhiteSpace(x.Value?.filename)
                    && x.Value.filename.Contains("veh_", StringComparison.OrdinalIgnoreCase))
              )
            ).ToList();

          if (vehicleModels.Count > 0)
            cameraModels = vehicleModels;
        }

        foreach (KeyValuePair<String, GR2> model in cameraModels) {
          if (model.Key.Contains("skeleton", StringComparison.OrdinalIgnoreCase)) continue;
          if (model.Value == null) continue;

          _focus = model.Value;

          // Transform all eight AABB corners.  Transforming only min/max is
          // not sufficient once a DYN visual has rotation: it can massively
          // underestimate the aggregate bounds and place the camera inside
          // the model.
          ExpandTransformedBounds(_focus, ref _globalBoxMin, ref _globalBoxMax);
        }
      }

      if (models != null) _models = models;
      if (resources != null) _resources = resources;

      _camera.Reset();
      _camera.Position = _cameraPos;
      _camera.LookAt(_cameraPos, _globalBoxCenter, Vector3.UnitY);

      // Fit the complete model into view using the real aggregate bounds.
      // The previous implementation started the minimum bounds at zero and
      // then multiplied the center by a large factor. That made many MNT/ITM
      // assets appear extremely close or even off-center.
      if (_globalBoxMin.X != Single.MaxValue && _globalBoxMax.X != Single.MinValue) {
        Vector3 boxSize = _globalBoxMax - _globalBoxMin;
        Single boxDiagonal = Math.Max(boxSize.Length(), 0.001F);
        _globalBoxCenter = _globalBoxMin + boxSize / 2.0F;

        // Fit from the bounding sphere and the actual vertical field of view
        // instead of using a large fixed multiplier.  This keeps tiny mounts
        // from filling the whole viewport without pushing large speeders/gliders
        // excessively far away.
        Single radius = Math.Max(boxDiagonal * 0.5F, 0.001F);
        Single verticalFov = 0.25F * MathF.PI;
        Single margin = type == "mnt" ? 1.30F : type == "itm" ? 1.22F : type == "dyn" ? 1.30F : 1.18F;
        Single distance = radius / (Single)Math.Tan(verticalFov * 0.5F) * margin;

        // Avoid pathological GOM/GR2 bounds while retaining a useful minimum
        // distance for very small objects.
        Single minDistance = type == "mnt" || type == "itm" ? 1.25F : 1.0F;
        Single maxDistance = Math.Max(boxDiagonal * 3.0F, minDistance);
        distance = Math.Max(minDistance, Math.Min(distance, maxDistance));

        Single beta = 0.42F;
        Single alpha = 0.5F;
        Single cosBeta = (Single)Math.Cos(beta);
        Vector3 direction = new Vector3(
          cosBeta * (Single)Math.Cos(alpha),
          (Single)Math.Sin(beta),
          cosBeta * (Single)Math.Sin(alpha)
        );
        _cameraPos = _globalBoxCenter + direction * distance;
        _camera.Position = _cameraPos;
        _camera.LookAt(_cameraPos, _globalBoxCenter, Vector3.UnitY);
      }

      foreach (KeyValuePair<String, GR2> model in models) {
        if (model.Value.materials?.Count > 0)
          foreach (GR2_Material material in model.Value.materials)
            material.ParseMAT(Device);

        if (model.Value.attachedModels.Count > 0)
          foreach (var attachModel in model.Value.attachedModels)
            if (attachModel.materials.Count > 0)
              foreach (GR2_Material attachMaterial in attachModel.materials)
                attachMaterial.ParseMAT(Device, model.Value.materials);
      }

      if (resources.Count > 0) {
        foreach (KeyValuePair<String, GR2> model in models) {
          if (model.Value.materials != null && model.Value.materials.Count > 0) {
            foreach (GR2_Material material in model.Value.materials) {
              if (material.derived == "HairC")
                if (resources.ContainsKey("appSlotHairColor"))
                  material.SetDynamicColor((GomObject)resources["appSlotHairColor"]);

              if (material.derived == "SkinB") {
                if (model.Value.filename.Contains("head_")) {
                  if (resources.ContainsKey("appSlotFacePaint"))
                    material.SetFacepaintMap(Device, (String)resources["appSlotFacePaint"]);

                  if (resources.ContainsKey("appSlotComplexion"))
                    material.SetComplexionMap(Device, (String)resources["appSlotComplexion"]);
                }

                if (resources.ContainsKey("appSlotSkinColor"))
                  material.SetDynamicColor((GomObject)resources["appSlotSkinColor"], 1);
              }

              if (material.derived == "Eye")
                if (resources.ContainsKey("appSlotEyeColor"))
                  material.SetDynamicColor((GomObject)resources["appSlotEyeColor"]);
            }
          }

          if (model.Value.attachedModels.Count > 0)
            foreach (GR2 attachModel in model.Value.attachedModels)
              if (attachModel.materials.Count > 0)
                foreach (GR2_Material attachMaterial in attachModel.materials) {
                  if (attachMaterial.derived == "HairC")
                    if (resources.ContainsKey("appSlotHairColor"))
                      attachMaterial.SetDynamicColor((GomObject)resources["appSlotHairColor"]);

                  if (attachMaterial.derived == "SkinB") {
                    if (model.Value.filename.Contains("head_")) {
                      if (resources.ContainsKey("appSlotFacePaint"))
                        attachMaterial.SetFacepaintMap(
                          Device,
                          (String)resources["appSlotFacePaint"]
                        );

                      if (resources.ContainsKey("appSlotComplexion"))
                        attachMaterial.SetComplexionMap(
                          Device,
                          (String)resources["appSlotComplexion"]
                        );
                    }

                    if (resources.ContainsKey("appSlotSkinColor"))
                      attachMaterial.SetDynamicColor((GomObject)resources["appSlotSkinColor"], 1);
                  }

                  if (attachMaterial.derived == "Eye")
                    if (resources.ContainsKey("appSlotEyeColor"))
                      attachMaterial.SetDynamicColor((GomObject)resources["appSlotEyeColor"]);
                }
        }
      }

      BuildGeometry();
    }
    public void MakeScreenshot(ImageFileFormat format) {
      try {
        String filename =
          Tools.PrepExtractPath(
            _fqn
            + '-'
            + DateTime.Now.ToString("yyyyMMddHHmmss")
            + '.'
            + format.ToString().ToLower());
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
        ((ModelBrowser)Window).StatusBarText("Screenshot Completed");
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine(ex.ToString());
      }
    }
    protected override void OnMouseDown(Object sender, MouseEventArgs e) {
      _lastMousePos = e.Location;
      Window.Controls.Find(RenderPanelName, true).First().Capture = true;
    }
    protected override void OnMouseMove(Object sender, MouseEventArgs e) {
      if ((Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left) {
        Single yDelta = MathF.ToRadians(0.4F * (e.Y - _lastMousePos.Y));
        Single xDelta = -MathF.ToRadians(0.4F * (e.X - _lastMousePos.X));

        if (Util.IsKeyDown(Keys.LShiftKey)) {
          xDelta = MathF.ToRadians(0.05f * (e.X - _lastMousePos.X));
          yDelta = MathF.ToRadians(0.05f * (e.Y - _lastMousePos.Y));

          _camera.Pan(-xDelta * _camera.Radius, yDelta * _camera.Radius);

        } else {
          _camera.Pitch(yDelta);
          _camera.Yaw(-xDelta);
        }

      } else if ((Control.MouseButtons & MouseButtons.Right) == MouseButtons.Right) {
        Single xDelta = MathF.ToRadians(0.05F * (e.X - _lastMousePos.X));
        Single yDelta = MathF.ToRadians(0.05F * (e.Y - _lastMousePos.Y));

        _camera.Pan(-xDelta * _camera.Radius, yDelta * _camera.Radius);
      }

      _lastMousePos = e.Location;
    }
    protected override void OnMouseUp(Object sender, MouseEventArgs e) {
      Window.Controls.Find(RenderPanelName, true).First().Capture = false;
    }
    protected override void OnMouseWheel(Object sender, MouseEventArgs e) {
      // Zoom relative to the current orbit radius.  The old geometric loop
      // changed Radius by only a few thousandths per wheel notch, which became
      // effectively invisible after the camera-fit changes.
      Single notches = e.Delta / 120.0F;
      if (notches == 0.0F) return;

      Single fraction = Util.IsKeyDown(Keys.ShiftKey) ? 0.025F : 0.12F;
      _cameraZoomSpeed = Math.Max(_camera.Radius * fraction, 0.01F);
      _camera.Zoom(-notches * _cameraZoomSpeed);
    }
    public override void OnResize() {
      base.OnResize();

      _camera.SetLens(0.25F * MathF.PI, AspectRatio, 0.001F, 1000.0F);
    }
    public void SetMaterial(GR2_Material selectedMaterial) {
      if (selectedMaterial == null) return;

      List<EffectTechnique> derivedList = new List<EffectTechnique>() {
        _fx.Generic,
        _fx.Eye,
        _fx.Garment,
        _fx.HairC,
        _fx.SkinB
      };

      if (derivedList.Any(x => _activeTech == x)) {
        switch (selectedMaterial.derived) {
          case "AnimatedUV":
          case "AnimatedUVAlphaBlend":
          case "Creature":
          case "DiffuseFlat":
          case "EmissiveOnly":
            _activeTech = _fx.Generic;
            break;
          case "Eye":
            _activeTech = _fx.Eye;
            break;
          case "Garment":
            _activeTech = _fx.Garment;
            break;
          case "Glass":
            _activeTech = _fx.Generic;
            break;
          case "HairC":
            _activeTech = _fx.HairC;
            break;
          case "HighQualityCharacter":
          case "Ice":
          case "NoShadeTexFogged":
          case "OpacityFade":
            _activeTech = _fx.Generic;
            break;
          case "SkinB":
            _activeTech = _fx.SkinB;
            break;
          case "Skydome":
          case "Uber":
          case "UberEnvBlend":
          case "Vegetation":
          default:
            _activeTech = _fx.Generic;
            break;
        }
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

      ImmediateContext.Rasterizer.State = selectedMaterial.isTwoSided
        ? RenderStates.TwoSidedRS
        : RenderStates.OneSidedRS;

      _fx.SetDiffuseMap(selectedMaterial.diffuseSRV);
      _fx.SetRotationMap(selectedMaterial.rotationSRV);
      _fx.SetGlossMap(selectedMaterial.glossSRV);
      _fx.SetPaletteMap(selectedMaterial.paletteSRV);
      _fx.SetPaletteMaskMap(selectedMaterial.paletteMaskSRV);

      _fx.SetComplexionMap(selectedMaterial.complexionSRV);
      _fx.SetFacepaintMap(selectedMaterial.facepaintSRV);
      _fx.SetAgeMap(selectedMaterial.ageSRV);

      // _fx.SetGlassParams(selectedMaterial.glassParams);

      _fx.SetPalette1(selectedMaterial.palette1);
      _fx.SetPalette2(selectedMaterial.palette2);

      _fx.SetPalette1Spec(selectedMaterial.palette1Spec);
      _fx.SetPalette2Spec(selectedMaterial.palette2Spec);

      _fx.SetPalette1MetSpec(selectedMaterial.palette1MetSpec);
      _fx.SetPalette2MetSpec(selectedMaterial.palette2MetSpec);

      _fx.SetFlushTone(selectedMaterial.flushTone);
      _fx.SetFleshBrightness(selectedMaterial.fleshBrightness);
    }

    public override void UpdateScene(Single dt) {
      base.UpdateScene(dt);

      if (Form.ActiveForm != null) {
        if (Util.IsKeyDown(Keys.R)) {
          _camera.Reset();
          _camera.Position = _cameraPos;
          _camera.LookAt(_cameraPos, _globalBoxCenter, Vector3.UnitY);
        }
      }

      System.Threading.Thread.Sleep(1); // Fix for UI lag. Sleeps the thread for 1 millisecond...
    }
  }
}
