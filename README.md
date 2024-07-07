# ImageToASCII
The intent of this program is to take an input image and convert it into ASCII art.

It uses the Structural Similarity Index (SSIM) in order to classify each NxN tile of the image as an ASCII glyph.

A component using a neural net is also being worked on, since comparing SSIMs between every tile and every glyph is very computationally expensive (slow).

## Command Line Arguments

- [Path]: The first argument is the path to the image to convert.
    - If pre-processing images for training, this can also be a directory
- -n, --no-color: (flag) Do not use ANSI coloring when printing to the console
- -i, --invert: (flag) Calculate SSIMs for white-on-black text instead of the default black-on-white
- -c, --clamp: WidthxHeight to clamp the image dimensions to
- --method: The way to determine the most similar glyph to an image tile
    - ssim (default) - Calculate the SSIM between the tile and every glyph, and use the best match
    - model - Use a trained neural network to select the best matching glyph to a tile
- --mode: The operation to perform
    - render (default) - Draw the ASCII image to the console
    - train - Train a model to predict the appropriate glyph for a tile
    - preprocess - Collect SSIM data for later training
- --model: If training, or using the trained model method, this is the path to the model
- --threads: The max degree of parallelism when calculating SSIMs or training a model
- --preprocess: When pre-processing, write data to this path. When training, load pre-processed data from this path.

### Pre-processing Arguments
Arguments that only apply for --mode preprocess

- --shuffle: (flag) Shuffle the ordering of pre-processed data. Take a LONG time on large datasets.

### Training Arguments
Arguments that only apply for --mode train

- --learning-rate: Coefficient to multiply gradients by when updating model weights
- --learning-rate-decay: Learning rate decays exponentially at this rate
- --alpha: Alpha value to use for Leaky ReLU
- --hidden-layers: Number of hidden layers to include in neural net
- --hidden-neurons: The number of neurons to include in each hidden layer
- --batch-size: Train neural net on mini-batches of this size

### Other Arguments
- -g, --glyphs: The path to the glyphs.txt file, if not using the default
- -t, --tile-size: The size of the image tiles to classify as ASCII glyphs
- -f, --font-face: When calculating the similarity between an image tile and an ASCII glyph, use this font
- -s, --subdivide: The number of times to subdivide an image tile while calculating the SSIM

Example Usage:

The following is an example of rendering an ASCII image:
```
> .\I2A dir\images\image.png --threads 32
```

The following is an example of pre-processing a directory of images for training:
```
> .\I2A dir\images
    --mode preprocess
    --threads 32
    --preprocess-path dir\preprocessed
    --tile-size 4
    --subdivide 4
```

The following is an example of training a model on the pre-processed image data:
```
> .\I2A --mode train 
    --threads 32 
    --preprocessed-path dir\preprocessed\preprocessed.txt
    --model dir\model
    --learning-rate 0.05 
    --learning-rate-decay 0 
    --alpha 0.005 
    --hidden-layers 2 
    --hidden-neurons 128 
    --batch-size 64
```

## TODO
- Try to make neural net actually work

## Random Image Generator

This component is there to connect to a Fooocus instance to generate images to train the ImageToASCII neural net on, once that works.