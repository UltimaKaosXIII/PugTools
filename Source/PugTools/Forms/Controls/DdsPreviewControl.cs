using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DevIL;
using DrawingColor = System.Drawing.Color;
using DrawingPointF = System.Drawing.PointF;

namespace PugTools {
  /// <summary>
  /// Interactive DDS preview used by the Asset Browser.
  ///
  /// Plain 2D textures support cursor-centred wheel zoom and mouse-drag panning.
  /// DDS cubemaps are rendered as an environment viewed from inside the cube;
  /// dragging rotates the camera and the mouse wheel changes the field of view.
  /// </summary>
  internal sealed class DdsPreviewControl : Control {
    private const Double MinImageZoom = 0.02;
    private const Double MaxImageZoom = 32.0;
    private const Double MinCubeFov = 18.0;
    private const Double MaxCubeFov = 110.0;
    private const Int32 InteractiveCubePixelBudget = 360000;
    private const Int32 FinalCubePixelBudget = 950000;

    private Bitmap m_image;
    private Double m_zoom = 1.0;
    private DrawingPointF m_imageOffset;
    private Boolean m_dragging;
    private Point m_lastMouse;
    private Boolean m_checkerboard = true;

    private CubeMapPixels m_cubeMap;
    private Bitmap m_cubeFrame;
    private Double m_cubeYaw;
    private Double m_cubePitch;
    private Double m_cubeFov = 70.0;
    private Int64 m_lastInteractiveCubeRenderTick;
    private CancellationTokenSource m_cubeRenderCancellation;
    private Int32 m_cubeRenderGeneration;

    internal DdsPreviewControl() {
      SetStyle(
        ControlStyles.AllPaintingInWmPaint
        | ControlStyles.UserPaint
        | ControlStyles.OptimizedDoubleBuffer
        | ControlStyles.ResizeRedraw,
        true
      );
      BackColor = DrawingColor.White;
      Cursor = Cursors.Default;
      TabStop = true;
    }

    internal Boolean IsCubeMap => m_cubeMap != null;

    internal Boolean Checkerboard {
      get => m_checkerboard;
      set {
        if (m_checkerboard == value) return;
        m_checkerboard = value;
        Invalidate();
      }
    }

    internal void SetBitmap(Bitmap bitmap) {
      CancelCubeRender();
      m_cubeMap = null;
      DisposeBitmap(ref m_cubeFrame);
      DisposeBitmap(ref m_image);
      m_image = bitmap;
      ResetView();
      Invalidate();
    }

    internal static CubeMapPixels PrepareCubeMap(IReadOnlyDictionary<CubeMapFace, ImageData> faces) {
      if (faces == null || faces.Count == 0) throw new ArgumentException("Cubemap contains no readable faces.", nameof(faces));
      return CubeMapPixels.FromImageData(faces);
    }

    internal void SetCubeMap(CubeMapPixels cube) {
      if (cube == null) throw new ArgumentNullException(nameof(cube));
      CancelCubeRender();
      DisposeBitmap(ref m_image);
      DisposeBitmap(ref m_cubeFrame);
      m_cubeMap = cube;
      m_cubeYaw = 0.0;
      m_cubePitch = 0.0;
      m_cubeFov = 70.0;
      Cursor = Cursors.SizeAll;
      RequestCubeRender(false);
      Invalidate();
    }

    internal void ClearPreview() {
      CancelCubeRender();
      m_cubeMap = null;
      DisposeBitmap(ref m_image);
      DisposeBitmap(ref m_cubeFrame);
      m_zoom = 1.0;
      m_imageOffset = DrawingPointF.Empty;
      Cursor = Cursors.Default;
      Invalidate();
    }

    internal void ResetView() {
      m_dragging = false;
      if (m_cubeMap != null) {
        m_cubeYaw = 0.0;
        m_cubePitch = 0.0;
        m_cubeFov = 70.0;
        Cursor = Cursors.SizeAll;
        RequestCubeRender(false);
        return;
      }

      Cursor = Cursors.Default;
      if (m_image == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) {
        m_zoom = 1.0;
        m_imageOffset = DrawingPointF.Empty;
        Invalidate();
        return;
      }

      Double fitX = ClientSize.Width / (Double)m_image.Width;
      Double fitY = ClientSize.Height / (Double)m_image.Height;
      m_zoom = Math.Min(1.0, Math.Min(fitX, fitY));
      m_zoom = Clamp(m_zoom, MinImageZoom, MaxImageZoom);
      CenterImage();
      Invalidate();
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        CancelCubeRender();
        DisposeBitmap(ref m_image);
        DisposeBitmap(ref m_cubeFrame);
      }
      base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) {
      Graphics g = pevent.Graphics;
      if (m_cubeMap != null) {
        g.Clear(DrawingColor.Black);
        return;
      }

      if (!m_checkerboard) {
        g.Clear(BackColor);
        return;
      }

      const Int32 tile = 16;
      using SolidBrush light = new SolidBrush(DrawingColor.White);
      using SolidBrush dark = new SolidBrush(DrawingColor.FromArgb(224, 224, 224));
      g.FillRectangle(light, ClientRectangle);
      for (Int32 y = 0; y < Height; y += tile) {
        for (Int32 x = 0; x < Width; x += tile) {
          if ((((x / tile) + (y / tile)) & 1) != 0)
            g.FillRectangle(dark, x, y, Math.Min(tile, Width - x), Math.Min(tile, Height - y));
        }
      }
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

      if (m_cubeMap != null) {
        Bitmap frame = m_cubeFrame;
        if (frame != null) {
          e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
          e.Graphics.DrawImage(frame, ClientRectangle);
        } else {
          using StringFormat fmt = new StringFormat {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
          };
          using Brush brush = new SolidBrush(DrawingColor.Gainsboro);
          e.Graphics.DrawString("Rendering cubemap ...", Font, brush, ClientRectangle, fmt);
        }
        return;
      }

      if (m_image == null) return;

      Single drawWidth = (Single)(m_image.Width * m_zoom);
      Single drawHeight = (Single)(m_image.Height * m_zoom);
      RectangleF destination = new RectangleF(m_imageOffset.X, m_imageOffset.Y, drawWidth, drawHeight);
      e.Graphics.InterpolationMode = m_zoom >= 1.0
        ? InterpolationMode.NearestNeighbor
        : InterpolationMode.HighQualityBicubic;
      e.Graphics.DrawImage(m_image, destination);
    }

    protected override void OnMouseEnter(EventArgs e) {
      base.OnMouseEnter(e);
      // WinForms normally sends MouseWheel to the focused control rather than strictly to the control
      // under the pointer. Focusing on hover makes wheel zoom work immediately after selecting a DDS.
      if (FindForm()?.ContainsFocus == true) Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      Focus();
      if (e.Button != MouseButtons.Left || (m_image == null && m_cubeMap == null)) return;
      m_dragging = true;
      m_lastMouse = e.Location;
      Capture = true;
      Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left || !m_dragging) return;
      m_dragging = false;
      Capture = false;
      Cursor = m_cubeMap != null ? Cursors.SizeAll : Cursors.Default;
      if (m_cubeMap != null) RequestCubeRender(false);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      if (!m_dragging) return;

      Int32 dx = e.X - m_lastMouse.X;
      Int32 dy = e.Y - m_lastMouse.Y;
      m_lastMouse = e.Location;

      if (m_cubeMap != null) {
        Double sensitivity = Math.Max(0.0014, m_cubeFov / 70.0 * 0.0045);
        m_cubeYaw += dx * sensitivity;
        m_cubePitch -= dy * sensitivity;
        m_cubePitch = Clamp(m_cubePitch, -Math.PI * 0.49, Math.PI * 0.49);
        RequestCubeRender(true);
      } else if (m_image != null) {
        m_imageOffset.X += dx;
        m_imageOffset.Y += dy;
        Invalidate();
      }
    }

    protected override void OnMouseWheel(MouseEventArgs e) {
      base.OnMouseWheel(e);
      if (m_cubeMap != null) {
        Double steps = e.Delta / 120.0;
        m_cubeFov = Clamp(m_cubeFov * Math.Pow(0.88, steps), MinCubeFov, MaxCubeFov);
        RequestCubeRender(false);
        return;
      }

      if (m_image == null) return;
      Double oldZoom = m_zoom;
      Double steps2 = e.Delta / 120.0;
      Double newZoom = Clamp(oldZoom * Math.Pow(1.18, steps2), MinImageZoom, MaxImageZoom);
      if (Math.Abs(newZoom - oldZoom) < 0.000001) return;

      Double imageX = (e.X - m_imageOffset.X) / oldZoom;
      Double imageY = (e.Y - m_imageOffset.Y) / oldZoom;
      m_zoom = newZoom;
      m_imageOffset = new DrawingPointF(
        (Single)(e.X - imageX * newZoom),
        (Single)(e.Y - imageY * newZoom)
      );
      Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) {
      base.OnMouseDoubleClick(e);
      if (e.Button == MouseButtons.Left) ResetView();
    }

    protected override void OnResize(EventArgs e) {
      base.OnResize(e);
      if (m_cubeMap != null) RequestCubeRender(false);
      else if (m_image != null && !m_dragging) {
        // Preserve the user's zoom level, but keep a newly loaded/fitted texture centred when the
        // preview pane itself changes size before the first interaction.
        if (m_zoom <= 1.0) CenterImageIfSmallerThanViewport();
        Invalidate();
      }
    }

    private void CenterImage() {
      if (m_image == null) return;
      Single width = (Single)(m_image.Width * m_zoom);
      Single height = (Single)(m_image.Height * m_zoom);
      m_imageOffset = new DrawingPointF((ClientSize.Width - width) / 2.0f, (ClientSize.Height - height) / 2.0f);
    }

    private void CenterImageIfSmallerThanViewport() {
      if (m_image == null) return;
      Single width = (Single)(m_image.Width * m_zoom);
      Single height = (Single)(m_image.Height * m_zoom);
      Single x = m_imageOffset.X;
      Single y = m_imageOffset.Y;
      if (width <= ClientSize.Width) x = (ClientSize.Width - width) / 2.0f;
      if (height <= ClientSize.Height) y = (ClientSize.Height - height) / 2.0f;
      m_imageOffset = new DrawingPointF(x, y);
    }

    private void RequestCubeRender(Boolean interactive) {
      CubeMapPixels cube = m_cubeMap;
      if (cube == null || IsDisposed || ClientSize.Width <= 1 || ClientSize.Height <= 1) return;

      if (interactive) {
        Int64 now = Environment.TickCount64;
        if (now - m_lastInteractiveCubeRenderTick < 33) return;
        m_lastInteractiveCubeRenderTick = now;
      }

      CancelCubeRender();
      CancellationTokenSource cts = new CancellationTokenSource();
      CancellationToken renderToken = cts.Token;
      m_cubeRenderCancellation = cts;
      Int32 generation = ++m_cubeRenderGeneration;
      Int32 clientWidth = ClientSize.Width;
      Int32 clientHeight = ClientSize.Height;
      Double yaw = m_cubeYaw;
      Double pitch = m_cubePitch;
      Double fov = m_cubeFov;
      Int32 budget = interactive ? InteractiveCubePixelBudget : FinalCubePixelBudget;

      Task.Run(() => RenderCubeMap(cube, clientWidth, clientHeight, yaw, pitch, fov, budget, renderToken), renderToken)
        .ContinueWith(task => {
          if (task.IsCanceled || task.IsFaulted || task.Result == null) return;
          Bitmap rendered = task.Result;
          if (IsDisposed || !IsHandleCreated) {
            rendered.Dispose();
            return;
          }

          try {
            BeginInvoke(new Action(() => {
              if (IsDisposed || generation != m_cubeRenderGeneration || renderToken.IsCancellationRequested) {
                rendered.Dispose();
                return;
              }
              Bitmap old = m_cubeFrame;
              m_cubeFrame = rendered;
              old?.Dispose();
              Invalidate();
            }));
          }
          catch {
            rendered.Dispose();
          }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CancelCubeRender() {
      CancellationTokenSource cts = m_cubeRenderCancellation;
      m_cubeRenderCancellation = null;
      if (cts == null) return;
      try { cts.Cancel(); } catch { }
      cts.Dispose();
    }

    private static Bitmap RenderCubeMap(
      CubeMapPixels cube,
      Int32 requestedWidth,
      Int32 requestedHeight,
      Double yaw,
      Double pitch,
      Double fovDegrees,
      Int32 pixelBudget,
      CancellationToken token) {

      if (requestedWidth <= 0 || requestedHeight <= 0) return null;
      Double pixels = (Double)requestedWidth * requestedHeight;
      Double scale = pixels > pixelBudget ? Math.Sqrt(pixelBudget / pixels) : 1.0;
      Int32 width = Math.Max(64, (Int32)Math.Round(requestedWidth * scale));
      Int32 height = Math.Max(64, (Int32)Math.Round(requestedHeight * scale));

      Bitmap output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
      BitmapData data = output.LockBits(
        new Rectangle(0, 0, width, height),
        ImageLockMode.WriteOnly,
        PixelFormat.Format32bppArgb
      );

      try {
        Double tanHalfFov = Math.Tan(fovDegrees * Math.PI / 360.0);
        Double aspect = width / (Double)height;
        Double sinYaw = Math.Sin(yaw);
        Double cosYaw = Math.Cos(yaw);
        Double sinPitch = Math.Sin(pitch);
        Double cosPitch = Math.Cos(pitch);

        unsafe {
          Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y => {
            if ((y & 15) == 0) token.ThrowIfCancellationRequested();
            Int32* row = (Int32*)((Byte*)data.Scan0 + y * data.Stride);
            Double localY = (1.0 - ((y + 0.5) / height) * 2.0) * tanHalfFov;

            for (Int32 x = 0; x < width; x++) {
              Double localX = ((((x + 0.5) / width) * 2.0) - 1.0) * aspect * tanHalfFov;
              Double localZ = 1.0;

              // Pitch around camera X, then yaw around world Y.
              Double py = cosPitch * localY - sinPitch * localZ;
              Double pz = sinPitch * localY + cosPitch * localZ;
              Double wx = cosYaw * localX + sinYaw * pz;
              Double wy = py;
              Double wz = -sinYaw * localX + cosYaw * pz;

              row[x] = cube.Sample(wx, wy, wz);
            }
          });
        }
      }
      catch {
        output.UnlockBits(data);
        output.Dispose();
        throw;
      }

      output.UnlockBits(data);
      return output;
    }

    private static Double Clamp(Double value, Double min, Double max) {
      if (value < min) return min;
      if (value > max) return max;
      return value;
    }

    private static void DisposeBitmap(ref Bitmap bitmap) {
      Bitmap old = bitmap;
      bitmap = null;
      old?.Dispose();
    }

    internal sealed class CubeMapPixels {
      private readonly FacePixels m_positiveX;
      private readonly FacePixels m_negativeX;
      private readonly FacePixels m_positiveY;
      private readonly FacePixels m_negativeY;
      private readonly FacePixels m_positiveZ;
      private readonly FacePixels m_negativeZ;
      private readonly FacePixels m_fallback;

      private CubeMapPixels(
        FacePixels positiveX,
        FacePixels negativeX,
        FacePixels positiveY,
        FacePixels negativeY,
        FacePixels positiveZ,
        FacePixels negativeZ) {
        m_positiveX = positiveX;
        m_negativeX = negativeX;
        m_positiveY = positiveY;
        m_negativeY = negativeY;
        m_positiveZ = positiveZ;
        m_negativeZ = negativeZ;
        m_fallback = positiveZ ?? negativeZ ?? positiveX ?? negativeX ?? positiveY ?? negativeY;
      }

      internal static CubeMapPixels FromImageData(IReadOnlyDictionary<CubeMapFace, ImageData> faces) {
        FacePixels Get(CubeMapFace face) {
          if (!faces.TryGetValue(face, out ImageData image) || image == null) return null;
          return FacePixels.FromImageData(image, true);
        }

        CubeMapPixels result = new CubeMapPixels(
          Get(CubeMapFace.PositiveX),
          Get(CubeMapFace.NegativeX),
          Get(CubeMapFace.PositiveY),
          Get(CubeMapFace.NegativeY),
          Get(CubeMapFace.PositiveZ),
          Get(CubeMapFace.NegativeZ)
        );
        if (result.m_fallback == null) throw new InvalidOperationException("DDS cubemap contains no supported pixel data.");
        return result;
      }

      internal Int32 Sample(Double x, Double y, Double z) {
        Double ax = Math.Abs(x);
        Double ay = Math.Abs(y);
        Double az = Math.Abs(z);
        FacePixels face;
        Double u;
        Double v;
        Double major;

        if (ax >= ay && ax >= az) {
          major = ax;
          if (x >= 0.0) {
            face = m_positiveX ?? m_fallback;
            u = -z / major;
            v = -y / major;
          } else {
            face = m_negativeX ?? m_fallback;
            u = z / major;
            v = -y / major;
          }
        } else if (ay >= ax && ay >= az) {
          major = ay;
          if (y >= 0.0) {
            face = m_positiveY ?? m_fallback;
            u = x / major;
            v = z / major;
          } else {
            face = m_negativeY ?? m_fallback;
            u = x / major;
            v = -z / major;
          }
        } else {
          major = az;
          if (z >= 0.0) {
            face = m_positiveZ ?? m_fallback;
            u = x / major;
            v = -y / major;
          } else {
            face = m_negativeZ ?? m_fallback;
            u = -x / major;
            v = -y / major;
          }
        }

        Double tx = (u + 1.0) * 0.5;
        Double ty = (v + 1.0) * 0.5;
        Int32 px = (Int32)(tx * (face.Width - 1) + 0.5);
        Int32 py = (Int32)(ty * (face.Height - 1) + 0.5);
        if (px < 0) px = 0;
        else if (px >= face.Width) px = face.Width - 1;
        if (py < 0) py = 0;
        else if (py >= face.Height) py = face.Height - 1;
        return face.Pixels[py * face.Width + px];
      }
    }

    private sealed class FacePixels {
      internal readonly Int32 Width;
      internal readonly Int32 Height;
      internal readonly Int32[] Pixels;

      private FacePixels(Int32 width, Int32 height, Int32[] pixels) {
        Width = width;
        Height = height;
        Pixels = pixels;
      }

      internal static FacePixels FromImageData(ImageData image, Boolean forceOpaque) {
        if (image == null || image.Data == null || image.Width <= 0 || image.Height <= 0)
          throw new InvalidOperationException("DDS face contains no image data.");
        if (image.DataType != DataType.UnsignedByte && image.DataType != DataType.Byte)
          throw new NotSupportedException("DDS cubemap pixel type is not 8-bit and cannot be previewed interactively.");

        Int32 components;
        switch (image.Format) {
          case DataFormat.RGB:
          case DataFormat.BGR:
            components = 3;
            break;
          case DataFormat.RGBA:
          case DataFormat.BGRA:
            components = 4;
            break;
          case DataFormat.Luminance:
          case DataFormat.Alpha:
            components = 1;
            break;
          case DataFormat.LuminanceAlpha:
            components = 2;
            break;
          default:
            throw new NotSupportedException("DDS cubemap pixel format is not supported by the interactive preview.");
        }

        Int32 width = image.Width;
        Int32 height = image.Height;
        Int32 expected = checked(width * height * components);
        if (image.Data.Length < expected)
          throw new InvalidDataException("DDS cubemap face contains less pixel data than expected.");

        Int32[] pixels = new Int32[width * height];
        Byte[] src = image.Data;
        Boolean flipY = image.Origin == OriginLocation.LowerLeft;

        for (Int32 y = 0; y < height; y++) {
          Int32 srcY = flipY ? height - 1 - y : y;
          Int32 srcOffset = srcY * width * components;
          Int32 dstOffset = y * width;

          for (Int32 x = 0; x < width; x++) {
            Int32 i = srcOffset + x * components;
            Byte r;
            Byte g;
            Byte b;
            Byte a = 255;

            switch (image.Format) {
              case DataFormat.RGB:
                r = src[i]; g = src[i + 1]; b = src[i + 2];
                break;
              case DataFormat.BGR:
                b = src[i]; g = src[i + 1]; r = src[i + 2];
                break;
              case DataFormat.RGBA:
                r = src[i]; g = src[i + 1]; b = src[i + 2]; a = src[i + 3];
                break;
              case DataFormat.BGRA:
                b = src[i]; g = src[i + 1]; r = src[i + 2]; a = src[i + 3];
                break;
              case DataFormat.Luminance:
                r = g = b = src[i];
                break;
              case DataFormat.Alpha:
                r = g = b = 255; a = src[i];
                break;
              case DataFormat.LuminanceAlpha:
                r = g = b = src[i]; a = src[i + 1];
                break;
              default:
                r = g = b = 0;
                break;
            }

            if (forceOpaque) a = 255;
            pixels[dstOffset + x] = (a << 24) | (r << 16) | (g << 8) | b;
          }
        }

        return new FacePixels(width, height, pixels);
      }
    }
  }
}
