using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using FeatureLevel = SharpDX.Direct3D.FeatureLevel;
using Point = System.Drawing.Point;  // Explicitly use System.Drawing.Point
using Buffer = System.Buffer;  // Explicitly use System.Buffer

namespace ChessKit
{
    /// <summary>
    /// GPU-accelerated screen capture using Desktop Duplication API
    /// Works with AMD, NVIDIA, and Intel GPUs
    /// Falls back to GDI+ if GPU capture fails
    /// </summary>
    public static class ScreenCapture
    {
        private static Device? _device;
        private static OutputDuplication? _duplicatedOutput;
        private static Texture2D? _screenTexture;
        private static bool _gpuCaptureAvailable = false;
        private static readonly object _gpuLock = new object();
        private static DateTime _lastInitAttempt = DateTime.MinValue;
        private static Rectangle _duplicatedOutputBounds = Rectangle.Empty;
        private static readonly bool _forceGdiCapture =
            Environment.GetEnvironmentVariable("CHESSKIT_FORCE_GDI") is "1" or "true" or "TRUE";

        // === Region-DXGI fast path state ===
        // We hold a single staging texture sized to the requested region.
        // When the region size changes (board moved/resized), recreate it.
        // When an unrecoverable error happens repeatedly, mark the path
        // broken for the rest of the session and use GDI permanently.
        private static Texture2D? _regionStagingTexture;
        private static int _regionStagingWidth = 0;
        private static int _regionStagingHeight = 0;
        private static int _regionDxgiFailures = 0;
        private static bool _regionDxgiBroken = false;
        private const int RegionDxgiMaxFailures = 3;
        private static Mat? _lastRegionMat;
        private static Rectangle _lastRegionMatRect = Rectangle.Empty;
        private static string _lastRegionCaptureBackend = "none";
        private static SharpDX.Mathematics.Interop.RawRectangle[] _dirtyRectBuffer = new SharpDX.Mathematics.Interop.RawRectangle[4096];
        private static OutputDuplicateMoveRectangle[] _moveRectBuffer = new OutputDuplicateMoveRectangle[1024];

        // Sentinel exception thrown when TryAcquireNextFrame times out. Means
        // "no new desktop frame since last call" — not a DXGI failure, just
        // nothing changed. Caller falls through to GDI for this one call.
        private sealed class DxgiFrameTimeoutException : Exception { }

        static ScreenCapture()
        {
            if (!_forceGdiCapture)
            {
                InitializeGpuCapture();
            }
            else
            {
                _lastRegionCaptureBackend = "GDI-forced";
            }
        }

        private static void InitializeGpuCapture()
        {
            try
            {
                // Create D3D11 device
                _device = new Device(SharpDX.Direct3D.DriverType.Hardware,
                    DeviceCreationFlags.None,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0);

                // Get DXGI device
                using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
                using var adapter = dxgiDevice.GetParent<Adapter>();

                // Get first output (primary monitor)
                using var output = adapter.GetOutput(0);
                using var output1 = output.QueryInterface<Output1>();
                var desktopBounds = output.Description.DesktopBounds;
                _duplicatedOutputBounds = Rectangle.FromLTRB(
                    desktopBounds.Left,
                    desktopBounds.Top,
                    desktopBounds.Right,
                    desktopBounds.Bottom);
                if (_duplicatedOutputBounds.Width <= 0 || _duplicatedOutputBounds.Height <= 0)
                    throw new InvalidOperationException("DXGI output reported empty desktop bounds");

                // Create desktop duplication
                _duplicatedOutput = output1.DuplicateOutput(_device);

                _gpuCaptureAvailable = true;
                DebugRuntime.WriteLine("[ScreenCapture] GPU acceleration initialized (Desktop Duplication API)");
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[ScreenCapture] GPU initialization failed, using GDI+: {ex.Message}");
                _gpuCaptureAvailable = false;
                CleanupGpu();
            }
        }

        /// <summary>
        /// Captures the primary screen using GPU if available, otherwise GDI+
        /// </summary>
        public static Bitmap CapturePrimaryScreen()
        {
            if (_gpuCaptureAvailable && Screen.PrimaryScreen?.Bounds == _duplicatedOutputBounds)
            {
                lock (_gpuLock)
                {
                    try
                    {
                        return CaptureScreenGpu();
                    }
                    catch (Exception ex)
                    {
                        // Reinitialize on error (GPU might have reset)
                        if ((DateTime.UtcNow - _lastInitAttempt).TotalSeconds > 5)
                        {
                            DebugRuntime.WriteLine($"[ScreenCapture] GPU capture failed, reinitializing: {ex.Message}");
                            CleanupGpu();
                            InitializeGpuCapture();
                            _lastInitAttempt = DateTime.UtcNow;
                        }
                    }
                }
            }

            return CaptureScreenGdi();
        }

        /// <summary>
        /// Captures the whole virtual desktop. The returned bitmap is local to
        /// <paramref name="bounds"/>: a detection at bitmap x/y must be offset
        /// by bounds.Left/Top to become real screen coordinates.
        /// Full-desktop scans are infrequent, so prefer correctness across
        /// negative-coordinate and secondary monitors over the single-output
        /// DXGI fast path.
        /// </summary>
        public static Bitmap CaptureVirtualScreen(out Rectangle bounds)
        {
            bounds = GetVirtualScreenBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            }

            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            _lastRegionCaptureBackend = "GDI-Virtual";
            return bmp;
        }

        /// <summary>
        /// Captures a specific region of the screen. Tries the DXGI Desktop
        /// Duplication path first (fast on high-DPI screens because it goes
        /// through the GPU compositor instead of GDI's slow framebuffer
        /// readback). Falls back to GDI/BitBlt on any failure.
        ///
        /// On a 5K monitor, GDI's CopyFromScreen takes ~25-50ms per frame even
        /// for a small region because the source desktop framebuffer is huge
        /// and GDI synchronizes against the DWM compositor. DXGI's
        /// CopyResource with a sub-region box transfers only the requested
        /// pixels and runs in 1-3ms regardless of source size.
        ///
        /// Failure path is permanent for the session — after N consecutive
        /// errors we stop attempting DXGI and use GDI from then on. This
        /// avoids paying try/catch overhead on every frame when DXGI is
        /// fundamentally broken in this environment.
        /// </summary>
        public static Bitmap CaptureRegion(Rectangle region)
        {
            if (_gpuCaptureAvailable && !_regionDxgiBroken && region.Width > 0 && region.Height > 0 && IsRegionInsideDuplicatedOutput(region))
            {
                lock (_gpuLock)
                {
                    if (!_regionDxgiBroken)  // re-check inside lock
                    {
                        try
                        {
                            return CaptureRegionDxgi(region);
                        }
                        catch (DxgiFrameTimeoutException)
                        {
                            // No new desktop frame since last call — just use
                            // GDI for this one frame. Not a real failure.
                            // Fall through to GDI fallback below.
                        }
                        catch (Exception ex)
                        {
                            _regionDxgiFailures++;
                            DebugRuntime.WriteLine($"[ScreenCapture] DXGI region capture failed ({_regionDxgiFailures}/{RegionDxgiMaxFailures}): {ex.Message}");

                            // Try to recover by reinitializing the duplication
                            // (maybe the device was lost). Only attempt at most
                            // every 5 seconds to avoid hammering on a broken
                            // device.
                            if ((DateTime.UtcNow - _lastInitAttempt).TotalSeconds > 5)
                            {
                                CleanupGpu();
                                DisposeRegionStaging();
                                InitializeGpuCapture();
                                _lastInitAttempt = DateTime.UtcNow;
                            }

                            if (_regionDxgiFailures >= RegionDxgiMaxFailures)
                            {
                                _regionDxgiBroken = true;
                                DebugRuntime.WriteLine($"[ScreenCapture] DXGI region capture marked broken; using GDI for the rest of the session");
                            }
                            // Fall through to GDI fallback below
                        }
                    }
                }
            }

            return CaptureRegionGdi(region);
        }

        /// <summary>
        /// Captures a screen region directly into an OpenCV BGR Mat. This
        /// avoids the live path's old Bitmap -> Mat roundtrip when the DXGI
        /// fast path is available.
        /// </summary>
        public static Mat CaptureRegionMat(Rectangle region)
        {
            if (_gpuCaptureAvailable && !_regionDxgiBroken && region.Width > 0 && region.Height > 0 && IsRegionInsideDuplicatedOutput(region))
            {
                lock (_gpuLock)
                {
                    if (!_regionDxgiBroken)
                    {
                        try
                        {
                            var mat = CaptureRegionDxgiMat(region);
                            CacheRegionMat(region, mat);
                            _lastRegionCaptureBackend = "DXGI-Mat";
                            return mat;
                        }
                        catch (DxgiFrameTimeoutException)
                        {
                            if (TryCloneCachedRegionMat(region, out var cached))
                            {
                                _lastRegionCaptureBackend = "DXGI-cache";
                                return cached;
                            }
                            // No cached pixels for this rectangle yet; fall
                            // through to GDI for this one frame.
                        }
                        catch (Exception ex)
                        {
                            _regionDxgiFailures++;
                            DebugRuntime.WriteLine($"[ScreenCapture] DXGI Mat region capture failed ({_regionDxgiFailures}/{RegionDxgiMaxFailures}): {ex.Message}");

                            if ((DateTime.UtcNow - _lastInitAttempt).TotalSeconds > 5)
                            {
                                CleanupGpu();
                                DisposeRegionStaging();
                                InitializeGpuCapture();
                                _lastInitAttempt = DateTime.UtcNow;
                            }

                            if (_regionDxgiFailures >= RegionDxgiMaxFailures)
                            {
                                _regionDxgiBroken = true;
                                DebugRuntime.WriteLine("[ScreenCapture] DXGI Mat region capture marked broken; using GDI for the rest of the session");
                            }
                        }
                    }
                }
            }

            using var bmp = CaptureRegionGdi(region);
            var fallback = BitmapConverter.ToMat(bmp);
            if (fallback.Channels() == 4)
            {
                var bgr = new Mat();
                Cv2.CvtColor(fallback, bgr, ColorConversionCodes.BGRA2BGR);
                fallback.Dispose();
                fallback = bgr;
            }
            _lastRegionCaptureBackend = "GDI-Mat";
            CacheRegionMat(region, fallback);
            return fallback;
        }

        /// <summary>
        /// Captures a region through GDI and returns a BGR Mat. Use this for
        /// window/board acquisition probes where correctness across DPI,
        /// secondary monitors, and negative virtual-desktop coordinates matters
        /// more than the DXGI single-output fast path.
        /// </summary>
        public static Mat CaptureRegionMatGdi(Rectangle region)
            => CaptureRegionMatGdi(region, out _);

        /// <summary>
        /// Captures a region through GDI and returns the actual clipped screen
        /// bounds that were captured. The requested region may extend beyond
        /// the virtual desktop (for example, a browser window half off-screen);
        /// callers that map detections back to screen coordinates must use
        /// <paramref name="actualRegion"/>, not the requested rectangle.
        /// </summary>
        public static Mat CaptureRegionMatGdi(Rectangle region, out Rectangle actualRegion)
        {
            using var bmp = CaptureRegionGdi(region, out actualRegion);
            var mat = BitmapConverter.ToMat(bmp);
            if (mat.Channels() == 4)
            {
                var bgr = new Mat();
                Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
                mat.Dispose();
                mat = bgr;
            }

            _lastRegionCaptureBackend = "GDI-Mat-Probe";
            CacheRegionMat(actualRegion, mat);
            return mat;
        }

        /// <summary>
        /// Fast region capture via DXGI Desktop Duplication. Uses a persistent
        /// staging texture (allocated to the region size, recreated only when
        /// the size changes) and CopyResource with a sub-region Box, so only
        /// the requested pixels get transferred GPU→CPU.
        /// </summary>
        private static Bitmap CaptureRegionDxgi(Rectangle region)
        {
            if (_duplicatedOutput == null || _device == null)
                throw new InvalidOperationException("GPU capture not initialized");

            // Clamp region to the duplicated output. AcquireNextFrame returns
            // a texture matching the desktop output size; reading outside that
            // box would be undefined.
            region = Rectangle.Intersect(region, _duplicatedOutputBounds);
            if (region.Width <= 0 || region.Height <= 0)
                return new Bitmap(1, 1);
            var localRegion = new Rectangle(
                region.X - _duplicatedOutputBounds.X,
                region.Y - _duplicatedOutputBounds.Y,
                region.Width,
                region.Height);

            SharpDX.DXGI.Resource? screenResource = null;
            OutputDuplicateFrameInformation frameInfo;
            bool frameAcquired = false;

            try
            {
                var acquireResult = _duplicatedOutput.TryAcquireNextFrame(100, out frameInfo, out screenResource);

                // WaitTimeout means "no desktop changes since the last frame".
                // This happens when the screen is static. It is NOT a failure
                // — DXGI is fine, there's just nothing new to give us. Throw
                // a sentinel exception so the caller falls through to GDI for
                // this one call without incrementing the failure counter.
                if (acquireResult.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                {
                    throw new DxgiFrameTimeoutException();
                }

                if (acquireResult.Failure || screenResource == null)
                    throw new InvalidOperationException($"Failed to acquire frame: {acquireResult.Code}");
                frameAcquired = true;

                using var screenTexture2D = screenResource.QueryInterface<Texture2D>();
                var srcDesc = screenTexture2D.Description;
                ValidateLocalDxgiRegion(localRegion, srcDesc.Width, srcDesc.Height);

                // Make sure our persistent staging texture matches the region
                // size. If size changed, dispose the old one and create a new
                // one of the right dimensions.
                EnsureRegionStagingTexture(region.Width, region.Height, srcDesc.Format);
                if (_regionStagingTexture == null)
                    throw new InvalidOperationException("Region staging texture creation failed");

                // Copy ONLY the requested sub-region GPU→staging.
                // ResourceRegion is exclusive on the right/bottom edges.
                var srcBox = new ResourceRegion
                {
                    Left = localRegion.X,
                    Top = localRegion.Y,
                    Front = 0,
                    Right = localRegion.Right,
                    Bottom = localRegion.Bottom,
                    Back = 1
                };
                _device.ImmediateContext.CopySubresourceRegion(
                    screenTexture2D, 0, srcBox,
                    _regionStagingTexture, 0, 0, 0, 0);

                // Map and copy to managed bitmap
                var mapSource = _device.ImmediateContext.MapSubresource(_regionStagingTexture, 0, MapMode.Read, MapFlags.None);
                try
                {
                    var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
                    var lockRect = new Rectangle(0, 0, region.Width, region.Height);
                    var bitmapData = bitmap.LockBits(lockRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);
                    try
                    {
                        unsafe
                        {
                            byte* srcPtr = (byte*)mapSource.DataPointer;
                            byte* dstPtr = (byte*)bitmapData.Scan0;
                            int bytesPerRow = region.Width * 4;
                            for (int y = 0; y < region.Height; y++)
                            {
                                Buffer.MemoryCopy(
                                    srcPtr + y * mapSource.RowPitch,
                                    dstPtr + y * bitmapData.Stride,
                                    bitmapData.Stride,
                                    bytesPerRow);
                            }
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(bitmapData);
                    }

                    // Successful capture — reset failure counter.
                    _regionDxgiFailures = 0;
                    return bitmap;
                }
                finally
                {
                    _device.ImmediateContext.UnmapSubresource(_regionStagingTexture, 0);
                }
            }
            finally
            {
                screenResource?.Dispose();
                if (frameAcquired)
                {
                    try { _duplicatedOutput?.ReleaseFrame(); } catch { }
                }
            }
        }

        private static Mat CaptureRegionDxgiMat(Rectangle region)
        {
            if (_duplicatedOutput == null || _device == null)
                throw new InvalidOperationException("GPU capture not initialized");

            region = Rectangle.Intersect(region, _duplicatedOutputBounds);
            if (region.Width <= 0 || region.Height <= 0)
                return new Mat(1, 1, MatType.CV_8UC3, Scalar.Black);
            var localRegion = new Rectangle(
                region.X - _duplicatedOutputBounds.X,
                region.Y - _duplicatedOutputBounds.Y,
                region.Width,
                region.Height);

            SharpDX.DXGI.Resource? screenResource = null;
            OutputDuplicateFrameInformation frameInfo;
            bool frameAcquired = false;

            try
            {
                var acquireResult = _duplicatedOutput.TryAcquireNextFrame(100, out frameInfo, out screenResource);

                if (acquireResult.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                    throw new DxgiFrameTimeoutException();

                if (acquireResult.Failure || screenResource == null)
                    throw new InvalidOperationException($"Failed to acquire frame: {acquireResult.Code}");
                frameAcquired = true;

                if (CanReuseCachedRegionFromFrameMetadata(region, localRegion, frameInfo))
                    throw new DxgiFrameTimeoutException();

                using var screenTexture2D = screenResource.QueryInterface<Texture2D>();
                var srcDesc = screenTexture2D.Description;
                ValidateLocalDxgiRegion(localRegion, srcDesc.Width, srcDesc.Height);

                EnsureRegionStagingTexture(region.Width, region.Height, srcDesc.Format);
                if (_regionStagingTexture == null)
                    throw new InvalidOperationException("Region staging texture creation failed");

                var srcBox = new ResourceRegion
                {
                    Left = localRegion.X,
                    Top = localRegion.Y,
                    Front = 0,
                    Right = localRegion.Right,
                    Bottom = localRegion.Bottom,
                    Back = 1
                };
                _device.ImmediateContext.CopySubresourceRegion(
                    screenTexture2D, 0, srcBox,
                    _regionStagingTexture, 0, 0, 0, 0);

                var mapSource = _device.ImmediateContext.MapSubresource(_regionStagingTexture, 0, MapMode.Read, MapFlags.None);
                try
                {
                    using var bgra = new Mat(region.Height, region.Width, MatType.CV_8UC4);
                    unsafe
                    {
                        byte* srcPtr = (byte*)mapSource.DataPointer;
                        byte* dstPtr = (byte*)bgra.DataPointer;
                        int bytesPerRow = region.Width * 4;
                        long dstStep = bgra.Step();
                        for (int y = 0; y < region.Height; y++)
                        {
                            Buffer.MemoryCopy(
                                srcPtr + y * mapSource.RowPitch,
                                dstPtr + y * dstStep,
                                dstStep,
                                bytesPerRow);
                        }
                    }

                    var bgr = new Mat();
                    Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);

                    _regionDxgiFailures = 0;
                    return bgr;
                }
                finally
                {
                    _device.ImmediateContext.UnmapSubresource(_regionStagingTexture, 0);
                }
            }
            finally
            {
                screenResource?.Dispose();
                if (frameAcquired)
                {
                    try { _duplicatedOutput?.ReleaseFrame(); } catch { }
                }
            }
        }

        private static void CacheRegionMat(Rectangle region, Mat mat)
        {
            try
            {
                _lastRegionMat?.Dispose();
                _lastRegionMat = mat.Clone();
                _lastRegionMatRect = region;
            }
            catch
            {
                _lastRegionMat?.Dispose();
                _lastRegionMat = null;
                _lastRegionMatRect = Rectangle.Empty;
            }
        }

        private static bool TryCloneCachedRegionMat(Rectangle region, out Mat mat)
        {
            if (_lastRegionMat != null
                && !_lastRegionMat.Empty()
                && _lastRegionMatRect == region)
            {
                mat = _lastRegionMat.Clone();
                return true;
            }

            mat = new Mat();
            return false;
        }

        private static bool CanReuseCachedRegionFromFrameMetadata(
            Rectangle screenRegion,
            Rectangle localRegion,
            OutputDuplicateFrameInformation frameInfo)
        {
            if (_lastRegionMat == null
                || _lastRegionMat.Empty()
                || _lastRegionMatRect != screenRegion
                || frameInfo.TotalMetadataBufferSize <= 0)
            {
                return false;
            }

            try
            {
                if (AnyDirtyRectIntersects(localRegion))
                    return false;

                if (AnyMoveRectIntersects(localRegion))
                    return false;

                return true;
            }
            catch
            {
                // Metadata is only an optimization. If DXGI refuses the call
                // or needs a bigger buffer than our generous hot-path buffers,
                // keep the old behavior and copy the region.
                return false;
            }
        }

        private static bool AnyDirtyRectIntersects(Rectangle region)
        {
            int requiredBytes;
            int bufferBytes = _dirtyRectBuffer.Length * Marshal.SizeOf<SharpDX.Mathematics.Interop.RawRectangle>();
            _duplicatedOutput!.GetFrameDirtyRects(bufferBytes, _dirtyRectBuffer, out requiredBytes);
            int rectSize = Marshal.SizeOf<SharpDX.Mathematics.Interop.RawRectangle>();
            int count = Math.Min(_dirtyRectBuffer.Length, requiredBytes / rectSize);

            for (int i = 0; i < count; i++)
            {
                if (RawRectangleIntersects(region, _dirtyRectBuffer[i]))
                    return true;
            }

            return false;
        }

        private static bool AnyMoveRectIntersects(Rectangle region)
        {
            int requiredBytes;
            int bufferBytes = _moveRectBuffer.Length * Marshal.SizeOf<OutputDuplicateMoveRectangle>();
            _duplicatedOutput!.GetFrameMoveRects(bufferBytes, _moveRectBuffer, out requiredBytes);
            int rectSize = Marshal.SizeOf<OutputDuplicateMoveRectangle>();
            int count = Math.Min(_moveRectBuffer.Length, requiredBytes / rectSize);

            for (int i = 0; i < count; i++)
            {
                var move = _moveRectBuffer[i];
                if (RawRectangleIntersects(region, move.DestinationRect))
                    return true;

                int width = move.DestinationRect.Right - move.DestinationRect.Left;
                int height = move.DestinationRect.Bottom - move.DestinationRect.Top;
                var sourceRect = new SharpDX.Mathematics.Interop.RawRectangle
                {
                    Left = move.SourcePoint.X,
                    Top = move.SourcePoint.Y,
                    Right = move.SourcePoint.X + width,
                    Bottom = move.SourcePoint.Y + height
                };

                if (RawRectangleIntersects(region, sourceRect))
                    return true;
            }

            return false;
        }

        private static bool RawRectangleIntersects(Rectangle region, SharpDX.Mathematics.Interop.RawRectangle raw)
        {
            return raw.Right > region.Left
                && raw.Left < region.Right
                && raw.Bottom > region.Top
                && raw.Top < region.Bottom;
        }

        /// <summary>
        /// Lazily allocates / reallocates the region staging texture to match
        /// the requested size. Reuse keeps per-frame allocation out of the
        /// hot path — board region size only changes when the user moves or
        /// resizes the chess board, which is rare.
        /// </summary>
        private static void EnsureRegionStagingTexture(int width, int height, Format format)
        {
            if (_regionStagingTexture != null
                && _regionStagingWidth == width
                && _regionStagingHeight == height)
            {
                return;
            }

            DisposeRegionStaging();

            var stagingDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            };

            _regionStagingTexture = new Texture2D(_device, stagingDesc);
            _regionStagingWidth = width;
            _regionStagingHeight = height;
        }

        private static void DisposeRegionStaging()
        {
            try { _regionStagingTexture?.Dispose(); } catch { }
            _regionStagingTexture = null;
            _regionStagingWidth = 0;
            _regionStagingHeight = 0;
            try { _lastRegionMat?.Dispose(); } catch { }
            _lastRegionMat = null;
            _lastRegionMatRect = Rectangle.Empty;
        }

        /// <summary>
        /// GPU-accelerated screen capture using Desktop Duplication
        /// </summary>
        private static Bitmap CaptureScreenGpu()
        {
            if (_duplicatedOutput == null || _device == null)
                throw new InvalidOperationException("GPU capture not initialized");

            SharpDX.DXGI.Resource? screenResource = null;
            OutputDuplicateFrameInformation duplicateFrameInformation;
            bool frameAcquired = false;

            try
            {
                // Get new frame
                var result = _duplicatedOutput.TryAcquireNextFrame(100, out duplicateFrameInformation, out screenResource);

                if (result.Failure || screenResource == null)
                {
                    throw new InvalidOperationException("Failed to acquire frame");
                }
                frameAcquired = true;

                // Query for texture
                using var screenTexture2D = screenResource.QueryInterface<Texture2D>();

                // Create a staging texture
                var textureDesc = screenTexture2D.Description;
                textureDesc.CpuAccessFlags = CpuAccessFlags.Read;
                textureDesc.Usage = ResourceUsage.Staging;
                textureDesc.OptionFlags = ResourceOptionFlags.None;
                textureDesc.BindFlags = BindFlags.None;

                using var stagingTexture = new Texture2D(_device, textureDesc);

                // Copy resource to staging texture
                _device.ImmediateContext.CopyResource(screenTexture2D, stagingTexture);

                // Map the staging texture
                var mapSource = _device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);

                // Create bitmap
                var bitmap = new Bitmap(textureDesc.Width, textureDesc.Height, PixelFormat.Format32bppRgb);
                var boundsRect = new Rectangle(0, 0, textureDesc.Width, textureDesc.Height);
                var bitmapData = bitmap.LockBits(boundsRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

                // Copy pixels
                unsafe
                {
                    var sourcePtr = (byte*)mapSource.DataPointer;
                    var destPtr = (byte*)bitmapData.Scan0;

                    for (int y = 0; y < textureDesc.Height; y++)
                    {
                        // Copy row by row (handling stride)
                        Buffer.MemoryCopy(
                            sourcePtr + y * mapSource.RowPitch,
                            destPtr + y * bitmapData.Stride,
                            bitmapData.Stride,
                            textureDesc.Width * 4);
                    }
                }

                bitmap.UnlockBits(bitmapData);
                _device.ImmediateContext.UnmapSubresource(stagingTexture, 0);

                return bitmap;
            }
            finally
            {
                screenResource?.Dispose();

                // Release only a frame that was actually acquired. Calling
                // ReleaseFrame after a timeout/failure can poison duplication.
                if (frameAcquired)
                {
                    try { _duplicatedOutput?.ReleaseFrame(); } catch { }
                }
            }
        }

        /// <summary>
        /// Traditional GDI+ screen capture (fallback)
        /// </summary>
        private static Bitmap CaptureScreenGdi()
        {
            var bounds = Screen.PrimaryScreen!.Bounds;
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            return bmp;
        }

        /// <summary>
        /// Traditional GDI+ region capture (fallback)
        /// </summary>
        private static Bitmap CaptureRegionGdi(Rectangle region)
            => CaptureRegionGdi(region, out _);

        private static Bitmap CaptureRegionGdi(Rectangle region, out Rectangle actualRegion)
        {
            var screenBounds = GetVirtualScreenBounds();
            region = Rectangle.Intersect(region, screenBounds);
            actualRegion = region;

            if (region.Width <= 0 || region.Height <= 0)
            {
                actualRegion = Rectangle.Empty;
                return new Bitmap(1, 1);
            }

            var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(region.Location, Point.Empty, region.Size);
            return bmp;
        }

        /// <summary>
        /// Optimized capture with padding
        /// </summary>
        public static Bitmap CaptureRegionWithPadding(Rectangle region, int padding)
        {
            Rectangle screenBounds = GetVirtualScreenBounds();
            var expandedRegion = new Rectangle(
                Math.Max(screenBounds.Left, region.X - padding),
                Math.Max(screenBounds.Top, region.Y - padding),
                region.Width + (padding * 2),
                region.Height + (padding * 2)
            );

            return CaptureRegion(expandedRegion);
        }

        private static Rectangle GetVirtualScreenBounds()
        {
            Rectangle bounds = Rectangle.Empty;
            foreach (var screen in Screen.AllScreens)
            {
                bounds = bounds.IsEmpty ? screen.Bounds : Rectangle.Union(bounds, screen.Bounds);
            }

            return bounds.IsEmpty
                ? (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080))
                : bounds;
        }

        private static bool IsRegionInsideDuplicatedOutput(Rectangle region)
        {
            return !_duplicatedOutputBounds.IsEmpty &&
                region.Left >= _duplicatedOutputBounds.Left &&
                region.Top >= _duplicatedOutputBounds.Top &&
                region.Right <= _duplicatedOutputBounds.Right &&
                region.Bottom <= _duplicatedOutputBounds.Bottom;
        }

        private static void ValidateLocalDxgiRegion(Rectangle region, int textureWidth, int textureHeight)
        {
            if (region.Left < 0 || region.Top < 0 ||
                region.Right > textureWidth || region.Bottom > textureHeight ||
                region.Width <= 0 || region.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(region),
                    $"DXGI region {region} is outside output texture {textureWidth}x{textureHeight}");
            }
        }

        /// <summary>
        /// Native Win32 method for fast region capture (alternative to GDI+)
        /// </summary>
        public static Bitmap CaptureRegionNative(Rectangle region)
        {
            IntPtr desktopDC = GetDC(IntPtr.Zero);
            IntPtr memoryDC = CreateCompatibleDC(desktopDC);

            IntPtr bitmap = CreateCompatibleBitmap(desktopDC, region.Width, region.Height);
            IntPtr oldBitmap = SelectObject(memoryDC, bitmap);

            BitBlt(memoryDC, 0, 0, region.Width, region.Height,
                   desktopDC, region.X, region.Y, CopyPixelOperation.SourceCopy);

            SelectObject(memoryDC, oldBitmap);
            DeleteDC(memoryDC);
            ReleaseDC(IntPtr.Zero, desktopDC);

            var result = Image.FromHbitmap(bitmap);
            DeleteObject(bitmap);

            return result as Bitmap ?? new Bitmap(1, 1);
        }

        /// <summary>
        /// Returns a short label describing which capture path region captures
        /// are currently using. Useful for the on-screen debug overlay so the
        /// user can see at a glance whether the GPU fast path is engaged.
        /// </summary>
        public static string GetCaptureMode()
        {
            if (!_gpuCaptureAvailable)
                return _forceGdiCapture ? "GDI (forced)" : "GDI (no GPU)";
            if (_regionDxgiBroken)
                return $"GDI (GPU broken after {_regionDxgiFailures} fails)";
            return string.IsNullOrWhiteSpace(_lastRegionCaptureBackend)
                ? "GPU (DXGI)"
                : $"GPU ({_lastRegionCaptureBackend})";
        }

        private static void CleanupGpu()
        {
            // Release resources that belong to the device before the device.
            DisposeRegionStaging();
            try
            {
                _screenTexture?.Dispose();
                _duplicatedOutput?.Dispose();
                _device?.Dispose();
            }
            catch { }
            finally
            {
                _screenTexture = null;
                _duplicatedOutput = null;
                _device = null;
                _gpuCaptureAvailable = false;
                _duplicatedOutputBounds = Rectangle.Empty;
            }
        }

        /// <summary>
        /// Call this when shutting down the application
        /// </summary>
        public static void Cleanup()
        {
            lock (_gpuLock)
            {
                CleanupGpu();
            }
        }

        // Win32 API declarations for native capture
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, CopyPixelOperation rop);
    }
}
