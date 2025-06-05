using System;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using ComputeSharp;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Erasing
{
    public enum ToolMode
    {
        Erase,
        Warp
    }

    public sealed partial class MainWindow : Window
    {
        private CanvasBitmap? loadedImage;
        private bool isErasing = false;
        private bool isPointerPressed = false;
        private float eraserRadius = 17f;
        private float imageScale;
        private float offsetX;
        private float offsetY;
        private Point currentPointerPosition;
        private bool showPointerCircle = false;
        private byte[]? shaderCode;
        private CanvasRenderTarget? eraseRenderTarget;
        private CanvasRenderTarget? tempRenderTarget;
        private CanvasDevice device;
        private Point? lastPointerPosition = null;
        private ReadWriteTexture2D<float4>? gpuTexture;
        private GraphicsDevice? graphicsDevice;
        private float4[] _originalPixels;

        // Warping related fields
        private ToolMode currentToolMode = ToolMode.Erase;
        private Point[,] controlPoints = new Point[3, 3];
        private Point[,] originalControlPoints = new Point[3, 3];
        private int selectedPointX = -1;
        private int selectedPointY = -1;
        private bool isDragging = false;
        private bool showGrid = true;
        private int gridSize = 50;
        private int dragQualityGridSize = 50;
        private bool useHighQualityRendering = true;
        private float imageWidth;
        private float imageHeight;
        private float scale;
        private ReadOnlyBuffer<Int2>? warpedBuffer;
        private ReadOnlyBuffer<Int2>? originalBuffer;


        public MainWindow()
        {
            this.InitializeComponent();
            device = CanvasDevice.GetSharedDevice();

            MainCanvas.SizeChanged += (s, e) => MainCanvas.Invalidate();
            MainCanvas.PointerPressed += OnPointerPressed;
            MainCanvas.PointerMoved += OnPointerMoved;
            MainCanvas.PointerReleased += OnPointerReleased;
        }

        private async void OnLoadImageClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".png");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                using IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
                loadedImage = await CanvasBitmap.LoadAsync(MainCanvas, stream);

                int width = (int)loadedImage.SizeInPixels.Width;
                int height = (int)loadedImage.SizeInPixels.Height;
                float dpi = loadedImage.Dpi;

                // Store image dimensions for warping
                imageWidth = width;
                imageHeight = height;

                // Create main erase render target
                eraseRenderTarget = new CanvasRenderTarget(MainCanvas, width, height, dpi);

                // Draw loaded image into eraseRenderTarget
                using (var ds = eraseRenderTarget.CreateDrawingSession())
                {
                    ds.Clear(Colors.Transparent);
                    ds.DrawImage(loadedImage);
                }

                // Create tempRenderTarget (required for erase effect)
                tempRenderTarget = new CanvasRenderTarget(MainCanvas, width, height, dpi);

                // Convert pixel bytes to float4[] and upload to GPU
                byte[] buffer = eraseRenderTarget.GetPixelBytes();
                float4[] pixels = new float4[width * height];
                for (int i = 0; i < pixels.Length; i++)
                {
                    int index = i * 4;
                    pixels[i] = new float4(
                        buffer[index + 2] / 255f, // R
                        buffer[index + 1] / 255f, // G
                        buffer[index + 0] / 255f, // B
                        buffer[index + 3] / 255f  // A
                    );
                }

                graphicsDevice = GraphicsDevice.GetDefault();
                gpuTexture = graphicsDevice.AllocateReadWriteTexture2D<float4>(width, height);
                gpuTexture.CopyFrom(pixels);

                // Initialize control points for warping
                InitializeControlPoints();
            }

            MainCanvas.Invalidate();
        }

        private void OnEraseClick(object sender, RoutedEventArgs e)
        {
            // if (currentToolMode == ToolMode.Warp && !ControlPointsAreUnchanged())
            // {
            //     ApplyWarpToGPUTexture((int)imageWidth, (int)imageHeight);
            // }
            currentToolMode = ToolMode.Erase;
            isErasing = true;
            MainCanvas.Invalidate();
        }

        private void OnWarpClick(object sender, RoutedEventArgs e)
        {
            currentToolMode = ToolMode.Warp;
            isErasing = false;

            // Make sure control points are properly initialized
            if (gpuTexture != null)
            {
                InitializeControlPoints();
            }

            MainCanvas.Invalidate();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (loadedImage == null) return;

            isPointerPressed = true;
            currentPointerPosition = e.GetCurrentPoint(MainCanvas).Position;

            if (currentToolMode == ToolMode.Erase && isErasing)
            {
                // Apply erase effect immediately on press
                float imageX = (float)(currentPointerPosition.X - offsetX) / imageScale;
                float imageY = (float)(currentPointerPosition.Y - offsetY) / imageScale;

                // Bounds check
                if (imageX >= 0 && imageX <= eraseRenderTarget.SizeInPixels.Width &&
                    imageY >= 0 && imageY <= eraseRenderTarget.SizeInPixels.Height)
                {
                    Vector2 eraseCenter = new Vector2(imageX, imageY);
                    ApplyEraseEffect(eraseCenter);
                }
            }
            else if (currentToolMode == ToolMode.Warp)
            {
                // Check if we clicked on a control point
                selectedPointX = -1;
                selectedPointY = -1;
                float minDistance = float.MaxValue;
                float clickThreshold = 15.0f;

                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        float offset = 10.0f;
                        float cx = (float)controlPoints[y, x].X;
                        float cy = (float)controlPoints[y, x].Y;

                        // Adjust for offset control points
                        if (x == 0) cx -= offset;
                        if (x == 2) cx += offset;
                        if (y == 0) cy -= offset;
                        if (y == 2) cy += offset;

                        float distance = (float)Math.Sqrt(
                            Math.Pow(currentPointerPosition.X - cx, 2) +
                            Math.Pow(currentPointerPosition.Y - cy, 2));

                        if (distance < clickThreshold && distance < minDistance)
                        {
                            minDistance = distance;
                            selectedPointX = x;
                            selectedPointY = y;
                        }
                    }
                }

                if (selectedPointX != -1 && selectedPointY != -1)
                {
                    isDragging = true;
                }
            }

            MainCanvas.Invalidate();
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (loadedImage == null || eraseRenderTarget == null)
                return;

            Point newPointerPosition = e.GetCurrentPoint(MainCanvas).Position;
            showPointerCircle = currentToolMode == ToolMode.Erase;

            if (currentToolMode == ToolMode.Erase && isErasing && isPointerPressed)
            {
                if (lastPointerPosition != null)
                {
                    // Interpolate between the last and current pointer positions
                    float dx = (float)(newPointerPosition.X - lastPointerPosition.Value.X);
                    float dy = (float)(newPointerPosition.Y - lastPointerPosition.Value.Y);
                    float distance = MathF.Sqrt(dx * dx + dy * dy);

                    int steps = (int)(distance / (eraserRadius / 2)); // spacing
                    for (int i = 0; i <= steps; i++)
                    {
                        float t = i / (float)steps;
                        float interpX = (float)(lastPointerPosition.Value.X + t * dx);
                        float interpY = (float)(lastPointerPosition.Value.Y + t * dy);

                        float imageX = (interpX - offsetX) / imageScale;
                        float imageY = (interpY - offsetY) / imageScale;

                        if (imageX >= 0 && imageX <= eraseRenderTarget.SizeInPixels.Width &&
                            imageY >= 0 && imageY <= eraseRenderTarget.SizeInPixels.Height)
                        {
                            ApplyEraseEffect(new Vector2(imageX, imageY));
                        }
                    }
                }

                lastPointerPosition = newPointerPosition;
            }
            else if (currentToolMode == ToolMode.Warp && isDragging && selectedPointX != -1 && selectedPointY != -1)
            {
                // Update the selected control point position
                controlPoints[selectedPointY, selectedPointX] = newPointerPosition;
                lastPointerPosition = null;
            }
            else
            {
                lastPointerPosition = null;
            }

            currentPointerPosition = newPointerPosition;
            MainCanvas.Invalidate();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            isPointerPressed = false;
            showPointerCircle = false;
            lastPointerPosition = null;
            isDragging = false;
            selectedPointX = -1;
            selectedPointY = -1;
            MainCanvas.Invalidate();
        }

        private void ApplyEraseEffect(Vector2 eraseCenter)
        {
            if (gpuTexture == null || graphicsDevice == null) return;

            float eraseRadius = eraserRadius / imageScale;

            graphicsDevice.For(gpuTexture.Width, gpuTexture.Height, new EraseShader
            {
                texture = gpuTexture,
                eraseCenter = new Float2(eraseCenter.X, eraseCenter.Y),
                eraseRadius = eraseRadius
            });


            MainCanvas.Invalidate(); // Triggers re-draw
        }

        private void InitializeControlPoints()
        {
            if (gpuTexture == null || MainCanvas == null)
                return;

            controlPoints = new Point[3, 3];
            originalControlPoints = new Point[3, 3];

            // Calculate scale and offset for current display
            scale = Math.Min(
                (float)(MainCanvas.ActualWidth / gpuTexture.Width),
                (float)(MainCanvas.ActualHeight / gpuTexture.Height));

            float displayWidth = gpuTexture.Width * scale;
            float displayHeight = gpuTexture.Height * scale;
            float displayOffsetX = (float)((MainCanvas.ActualWidth - displayWidth) / 2);
            float displayOffsetY = (float)((MainCanvas.ActualHeight - displayHeight) / 2);

            // Store these for later use in warp calculations
            imageScale = scale;
            offsetX = displayOffsetX;
            offsetY = displayOffsetY;

            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    float normalizedX = x / 2.0f;
                    float normalizedY = y / 2.0f;

                    double posX = displayOffsetX + normalizedX * displayWidth;
                    double posY = displayOffsetY + normalizedY * displayHeight;

                    controlPoints[y, x] = new Point(posX, posY);
                    originalControlPoints[y, x] = new Point(posX, posY);
                }
            }
        }

        private Point BicubicInterpolate(Point[,] grid, float u, float v)
        {
            u = Math.Clamp(u, 0.001f, 0.999f);
            v = Math.Clamp(v, 0.001f, 0.999f);

            float uPos = u * 2;
            float vPos = v * 2;

            int x = (int)Math.Floor(uPos);
            int y = (int)Math.Floor(vPos);

            float uFrac = uPos - x;
            float vFrac = vPos - y;

            Point GetPoint(int i, int j)
            {
                i = Math.Clamp(i, 0, 2);
                j = Math.Clamp(j, 0, 2);
                return grid[j, i];
            }

            double Cubic(double p0, double p1, double p2, double p3, double t)
            {
                return p1 + 0.5 * t * (p2 - p0 +
                       t * (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3 +
                       t * (3.0 * (p1 - p2) + p3 - p0)));
            }

            // Interpolate rows
            double[] rowX = new double[4];
            double[] rowY = new double[4];

            for (int j = -1; j <= 2; j++)
            {
                double[] px = new double[4];
                double[] py = new double[4];
                for (int i = -1; i <= 2; i++)
                {
                    Point pt = GetPoint(x + i, y + j);
                    px[i + 1] = pt.X;
                    py[i + 1] = pt.Y;
                }
                rowX[j + 1] = Cubic(px[0], px[1], px[2], px[3], uFrac);
                rowY[j + 1] = Cubic(py[0], py[1], py[2], py[3], uFrac);
            }

            double finalX = Cubic(rowX[0], rowX[1], rowX[2], rowX[3], vFrac);
            double finalY = Cubic(rowY[0], rowY[1], rowY[2], rowY[3], vFrac);

            return new Point(finalX, finalY);
        }

        private bool ControlPointsAreUnchanged()
        {
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    if (controlPoints[y, x] != originalControlPoints[y, x])
                        return false;
                }
            }
            return true;
        }

        private void ApplyWarpToGPUTexture(int newWidth, int newHeight)
        {
            if (gpuTexture == null || graphicsDevice == null) return;

            using var sourceTexture = graphicsDevice.AllocateReadOnlyTexture2D<float4>(gpuTexture.Width, gpuTexture.Height);
            sourceTexture.CopyFrom(gpuTexture);

            var warpedPoints = new Int2[9];
            var originalPoints = new Int2[9];

            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    Point warped = controlPoints[y, x];
                    Point original = originalControlPoints[y, x];

                    warpedPoints[y * 3 + x] = new Int2((int)warped.X, (int)warped.Y);
                    originalPoints[y * 3 + x] = new Int2((int)original.X, (int)original.Y);
                }
            }

            warpedBuffer?.Dispose();
            originalBuffer?.Dispose();

            warpedBuffer = graphicsDevice.AllocateReadOnlyBuffer(warpedPoints);
            originalBuffer = graphicsDevice.AllocateReadOnlyBuffer(originalPoints);

            // Allocate a new gpuTexture with the new dimensions
            var newGpuTexture = graphicsDevice.AllocateReadWriteTexture2D<float4>(newWidth, newHeight);

            // Run the warp shader to fill the new texture
            graphicsDevice.For(newWidth, newHeight, new WarpShader(
                warpedBuffer,
                originalBuffer,
                newGpuTexture,
                sourceTexture,
                imageScale,
                newWidth,
                newHeight
            ));

            // Replace the old gpuTexturerkspa
            gpuTexture.Dispose();
            gpuTexture = newGpuTexture;

            // Update imageWidth and imageHeight
            imageWidth = newWidth;
            imageHeight = newHeight;
        }
        private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (gpuTexture == null) return;

            args.DrawingSession.Clear(Colors.LightGray);

            // Calculate scale and offset
            imageScale = Math.Min(
                (float)(sender.ActualWidth / gpuTexture.Width),
                (float)(sender.ActualHeight / gpuTexture.Height));

            offsetX = (float)((sender.ActualWidth - gpuTexture.Width * imageScale) / 2);
            offsetY = (float)((sender.ActualHeight - gpuTexture.Height * imageScale) / 2);

            // Create a temporary CanvasBitmap from GPU texture
            var tempBuffer = new float4[gpuTexture.Width * gpuTexture.Height];
            gpuTexture.CopyTo(tempBuffer);

            byte[] output = new byte[tempBuffer.Length * 4];
            for (int i = 0; i < tempBuffer.Length; i++)
            {
                int index = i * 4;
                output[index + 0] = (byte)(Math.Clamp(tempBuffer[i].Z, 0, 1) * 255); // B
                output[index + 1] = (byte)(Math.Clamp(tempBuffer[i].Y, 0, 1) * 255); // G
                output[index + 2] = (byte)(Math.Clamp(tempBuffer[i].X, 0, 1) * 255); // R
                output[index + 3] = (byte)(Math.Clamp(tempBuffer[i].W, 0, 1) * 255); // A
            }

            using var canvasBitmap = CanvasBitmap.CreateFromBytes(device, output,
                gpuTexture.Width, gpuTexture.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);

            if (!ControlPointsAreUnchanged())
            {
                DrawWarpedImage(args.DrawingSession, canvasBitmap);
            }
            else
            {
                // Draw normal image
                args.DrawingSession.DrawImage(canvasBitmap,
                    new Rect(offsetX, offsetY,
                        gpuTexture.Width * imageScale,
                        gpuTexture.Height * imageScale));
            }

            // Draw control points and grid for warp mode
            if (currentToolMode == ToolMode.Warp)
            {
                DrawControlPointsAndGrid(args.DrawingSession);
            }

            // Draw the pointer circle for erase mode
            if (currentToolMode == ToolMode.Erase && isErasing && showPointerCircle)
            {
                args.DrawingSession.FillCircle((float)currentPointerPosition.X,
                                               (float)currentPointerPosition.Y,
                                               eraserRadius,
                                               Microsoft.UI.ColorHelper.FromArgb(64, 255, 0, 0));
                args.DrawingSession.DrawCircle((float)currentPointerPosition.X,
                                               (float)currentPointerPosition.Y,
                                               eraserRadius,
                                               Colors.Red, 2f);
            }
        }

        private void DrawWarpedImage(CanvasDrawingSession ds, CanvasBitmap bitmap)
        {
            if (bitmap == null) return;

            int currentGridSize = isDragging ? dragQualityGridSize : gridSize;
            float step = 1f / currentGridSize;

            var interpolationMode = isDragging || !useHighQualityRendering
                ? CanvasImageInterpolation.Linear
                : CanvasImageInterpolation.HighQualityCubic;

            for (int y = 0; y < currentGridSize; y++)
            {
                for (int x = 0; x < currentGridSize; x++)
                {
                    float u0 = x * step;
                    float v0 = y * step;
                    float u1 = (x + 1) * step;
                    float v1 = (y + 1) * step;

                    Rect sourceRect = new Rect(
                        u0 * imageWidth,
                        v0 * imageHeight,
                        (u1 - u0) * imageWidth,
                        (v1 - v0) * imageHeight
                    );

                    Point p00 = BicubicInterpolate(controlPoints, u0, v0);
                    Point p10 = BicubicInterpolate(controlPoints, u1, v0);
                    Point p01 = BicubicInterpolate(controlPoints, u0, v1);
                    Point p11 = BicubicInterpolate(controlPoints, u1, v1);

                    double minX = Math.Min(Math.Min(p00.X, p10.X), Math.Min(p01.X, p11.X));
                    double minY = Math.Min(Math.Min(p00.Y, p10.Y), Math.Min(p01.Y, p11.Y));
                    double maxX = Math.Max(Math.Max(p00.X, p10.X), Math.Max(p01.X, p11.X));
                    double maxY = Math.Max(Math.Max(p00.Y, p10.Y), Math.Max(p01.Y, p11.Y));

                    Rect destRect = new Rect(minX, minY, maxX - minX, maxY - minY);

                    try
                    {
                        ds.DrawImage(
                            bitmap,
                            destRect,
                            sourceRect,
                            1.0f,
                            interpolationMode
                        );
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Warped draw error: {ex.Message}");

                        if (x == 0 && y == 0)
                        {
                            ds.DrawImage(bitmap,
                                new Rect(offsetX, offsetY,
                                    gpuTexture.Width * imageScale,
                                    gpuTexture.Height * imageScale));
                        }
                    }
                }
            }
        }

        private void DrawControlPointsAndGrid(CanvasDrawingSession ds)
        {
            float offset = 10.0f;

            if (showGrid)
            {
                // Draw horizontal grid lines
                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        ds.DrawLine(
                            (float)controlPoints[y, x].X,
                            (float)controlPoints[y, x].Y,
                            (float)controlPoints[y, x + 1].X,
                            (float)controlPoints[y, x + 1].Y,
                            Colors.LightBlue, 1f);
                    }
                }

                // Draw vertical grid lines
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        ds.DrawLine(
                            (float)controlPoints[y, x].X,
                            (float)controlPoints[y, x].Y,
                            (float)controlPoints[y + 1, x].X,
                            (float)controlPoints[y + 1, x].Y,
                            Colors.LightBlue, 1f);
                    }
                }
            }

            // Draw control points
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    float radius = (selectedPointX == x && selectedPointY == y) ? 8.0f : 6.0f;
                    var color = (selectedPointX == x && selectedPointY == y)
                        ? Colors.Red
                        : (x == 1 && y == 1) ? Colors.Orange : Colors.Blue;

                    float cx = (float)controlPoints[y, x].X;
                    float cy = (float)controlPoints[y, x].Y;

                    // Move corners and edges outside the image
                    if (x == 0) cx -= offset;         // Left column
                    if (x == 2) cx += offset;         // Right column
                    if (y == 0) cy -= offset;         // Top row
                    if (y == 2) cy += offset;         // Bottom row

                    ds.FillCircle(cx, cy, radius, color);
                }
            }
        }
    }
}
