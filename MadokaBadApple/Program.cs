using NAudio.Wave;
using OpenCvSharp;

const string grayScale = "$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\\|()1{}[]?-_+~<>i!lI;:,\"^`'. ";
const string videoPath = @"madoka-magica-bad-apple.mp4";
const string audioPath = @"bad-apple.mp3";

const int outputWidth = 50;

using var capture = new VideoCapture(videoPath);
if (!capture.IsOpened())
{
    Console.WriteLine("Error: Video file could not be opened.");
    return;
}

using (var audioOutput = new WaveOutEvent())
using (var audioFile = new AudioFileReader(audioPath))
{
    do
    {
        using var frameMat = new Mat();
        if (!capture.Read(frameMat) || frameMat.Empty())
        {
            capture.Set(VideoCaptureProperties.PosFrames, 0);
            continue;
        }

        var asciiArt = ConvertToAscii(frameMat, outputWidth);

        if (!audioOutput.PlaybackState.Equals(PlaybackState.Playing))
        {
            audioOutput.Init(audioFile);
            audioOutput.Play();
        }

        Console.Clear();
        Console.WriteLine(asciiArt);
        Thread.Sleep(26);
    } while (true);
}

string ConvertToAscii(Mat frame, int outputWidth)
{
    var scaleFactor = outputWidth / (double)frame.Width;
    var outputHeight = (int)(frame.Height * scaleFactor);

    Cv2.Resize(frame, frame, new Size(outputWidth, outputHeight));

    Cv2.CvtColor(frame, frame, ColorConversionCodes.BGR2GRAY);

    var asciiArt = "";
    for (int y = 0; y < frame.Rows; y++)
    {
        for (int x = 0; x < frame.Cols; x++)
        {
            byte color = frame.At<byte>(y, x);
            int index = color * (grayScale.Length - 1) / 255;
            asciiArt += grayScale[index];
        }
        asciiArt += "\n";
    }

    return asciiArt;
}
