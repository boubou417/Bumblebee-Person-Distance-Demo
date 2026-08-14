# Lightweight Detection Test

This branch replaces YOLO pose inference with YOLO person detection while keeping the polished exhibition UI, asynchronous capture/display pipeline, distance smoothing, temporal confirmation, duplicate suppression, F3 diagnostics, F11 maximize, and title-bar double-click behavior.

## Model selection

The application automatically tries the following models in order:

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
