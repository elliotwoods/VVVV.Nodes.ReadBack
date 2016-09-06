![Async node help patch](https://github.com/elliotwoods/VVVV.Nodes.ReadBack/blob/master/Capture.PNG?raw=true.jpg)

This pack contains 2 nodes:

* Writer (DX11.Texture2D Parallel)
* Writer (DX11.Texture2D Async)
* to accelerate workflows when you have to save many textures to disk quickly. This node generally supports all formats supported by the standard Writer node (e.g. recently 16bit PNG's), and uses the same functions internally.

## Parallel
Accepts a spread of textures and saves them to disk in separate threads. The main thread blocks until all save operations are complete

## Async
Accepts a spread of textures and saves them to disk in separate threads. The main thread is not used for saving, and is allowed to continue. Later when any save operation is completed, the node will output the result of that save operation on its output.

Generally we restrict the queue size to ensure that we don't crash the GPU with too many save devices. Also adding more than 8-16 save devices doesn't offer any performance gains.
