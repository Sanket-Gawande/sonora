using System;

namespace Sonora {
 // Converts any capture format to 48 kHz stereo: channels are mapped first (mono is duplicated,
 // extra channels beyond front left/right are dropped), then linear interpolation changes the rate.
 // Linear is enough for a first pass; a windowed-sinc filter can replace it without touching callers.
 public sealed class Resampler {
  readonly int inputRate, inputChannels;
  readonly double step;
  double position;
  float lastLeft, lastRight;
  bool primed;
  float[] output = new float[0];

  public Resampler(int inputRate, int inputChannels) {
   this.inputRate = inputRate;
   this.inputChannels = inputChannels;
   step = inputRate / 48000.0;
  }

  public bool IsPassThrough { get { return inputRate == 48000 && inputChannels == 2; } }

  // Returns interleaved stereo at 48 kHz; `frames` receives the output frame count.
  public float[] Process(float[] input, int inputFrames, out int frames) {
   if (IsPassThrough) { frames = inputFrames; return input; }
   int capacity = (int)Math.Ceiling((inputFrames + 1) / step) + 2;
   if (output.Length < capacity * 2) output = new float[capacity * 2];
   frames = 0;
   // `position` is measured in input frames, where -1 is the last frame of the previous block.
   while (true) {
    int index = (int)Math.Floor(position);
    if (index + 1 >= inputFrames) break;
    double t = position - index;
    float l0, r0, l1, r1;
    Frame(input, index, out l0, out r0);
    Frame(input, index + 1, out l1, out r1);
    output[frames * 2] = (float)(l0 + (l1 - l0) * t);
    output[frames * 2 + 1] = (float)(r0 + (r1 - r0) * t);
    frames++;
    position += step;
   }
   position -= inputFrames;
   if (inputFrames > 0) { Frame(input, inputFrames - 1, out lastLeft, out lastRight); primed = true; }
   return output;
  }

  void Frame(float[] input, int index, out float left, out float right) {
   if (index < 0) { left = primed ? lastLeft : 0; right = primed ? lastRight : 0; return; }
   int o = index * inputChannels;
   left = input[o];
   right = inputChannels > 1 ? input[o + 1] : left;
  }
 }
}
