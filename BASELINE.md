# V1.0 Baseline

This branch preserves the initial Bumblebee person-distance demo structure before optimization work.

Runtime files intentionally not committed:
- `yolov8n.onnx`
- `coco.names`
- `APO_LOGO2.jpg`
- FLIR/Spinnaker runtime DLLs under `Libraries`

Primary behavior:
- Connect to FLIR Bumblebee
- Acquire rectified + disparity streams
- YOLOv8n COCO person detection
- NMS person boxes
- Median stereo distance estimate
- Optional disparity heatmap display

Optimization work should be performed on a separate branch.
