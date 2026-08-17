# Lightweight Detection Test

This branch uses YOLO person detection instead of YOLO pose inference while keeping the polished exhibition UI, asynchronous capture/display pipeline, distance smoothing, temporal confirmation, duplicate suppression, F3 diagnostics, F11 maximize, and title-bar double-click behavior.

## Source layout

The lightweight branch now uses detection-only names so it is easy to tell which code is active:

- `Form1.AsyncDetection.cs` — ONNX Runtime model loading, tensor preparation, person inference and NMS.
- `Form1.DetectionTemporalFilter.cs` — two-hit confirmation, short miss tolerance and duplicate-box suppression.
- `Form1.CaptureResilience.cs` — Bumblebee capture/retry pipeline and detection frame queueing.
- `Form1.UiPolish.cs` — colored person boxes and distance badges.
- `Form1.PerformanceOverlay.cs` — F3 timing/CPU/detection statistics.
- `Form1.cs` — camera/UI basics and disparity distance tracking.

The obsolete `Form1.AsyncPose.cs`, `Form1.PoseTemporalFilter.cs`, pose model constants, keypoint structures and skeleton drawing code are not part of this branch anymore.

## Model selection

The actual model selection is at the top of `Form1.AsyncDetection.cs`:

```csharp
private const string PreferredDetectionModelFile = "yolov8n-512.onnx";
private const string FallbackDetectionModelFile = "yolov8n.onnx";
```

The application automatically tries them in this order:

1. `yolov8n-512.onnx` — preferred for the CPU-lightweight comparison.
2. `yolov8n.onnx` — fallback for compatibility with the original demo.

For the fairest comparison with the previous 512-pixel pose build, export the detection model at 512 pixels:

```bash
yolo export model=yolov8n.pt format=onnx imgsz=512 opset=12 simplify=True
```

Rename/copy the exported file as `yolov8n-512.onnx` beside the application executable/model files.

The F3 performance overlay shows the model name and actual input size in use, so it is easy to verify whether the 512 model or fallback model was loaded.

## What to compare

With the same cameras and stereo resolution, compare `Process CPU`, `Inference`, `Detect FPS`, `Camera FPS`, and `Display FPS` against the pose branch. The visual output is a colored person box plus the existing smoothed distance badge instead of a 17-keypoint skeleton.
