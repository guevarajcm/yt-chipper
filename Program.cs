using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

class Program
{
    private static string FfmpegPath = "ffmpeg";

    static async Task Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run <URL> [output.mp4] [start_time] [end_time]");
            return;
        }

        string url = args[0];
        string outputFile = args.Length > 1 ? args[1] : "video.mp4";

        if (!outputFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            outputFile += ".mp4";
        }

        string? rawStart = args.Length > 2 ? args[2] : null;
        string? rawEnd = args.Length > 3 ? args[3] : null;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("\n❌ Operation cancelled by user.");
            cts.Cancel();
            e.Cancel = true;
        };

        if (!IsFfmpegAvailable())
        {
            Console.WriteLine("❌ Error: 'ffmpeg' is not installed or not found in your PATH.");
            return;
        }

        string? startTime = ParseTime(rawStart);
        string? endTime = ParseTime(rawEnd);

        if (startTime != null && endTime != null && TimeSpan.Parse(startTime) >= TimeSpan.Parse(endTime))
        {
            Console.WriteLine("❌ Start time must be before end time.");
            return;
        }

        bool needsTrimming = startTime != null && endTime != null;

        if (File.Exists(outputFile))
        {
            Console.Write($"⚠️ File '{outputFile}' already exists. Overwrite? (y/N): ");
            var key = Console.ReadKey().KeyChar;
            Console.WriteLine();
            if (char.ToLower(key) != 'y')
            {
                outputFile = GetUniqueFileName(outputFile);
                Console.WriteLine($"💾 New file will be saved as: {outputFile}");
            }
        }

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36"
        );

        var youtube = new YoutubeClient(httpClient);

        try
        {
            var video = await youtube.Videos.GetAsync(url);
            var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);

            var muxedStreams = streamManifest.GetMuxedStreams()
                .Where(s => s.Container.Name == "mp4")
                .OrderByDescending(s => s.VideoQuality.MaxHeight);

            string fullOutputFile = outputFile;

            if (muxedStreams.Any())
            {
                var muxed = muxedStreams.First();
                Console.WriteLine($"🎥 Downloading (muxed): {video.Title}");
                Console.Write($"⬇️ Downloading:    ");
                await youtube.Videos.Streams.DownloadAsync(muxed, fullOutputFile, progress: new Progress<double>(p => Console.Write($"\r⬇️ Downloading: {p:P0}    ")), cancellationToken: cts.Token);
                Console.WriteLine("\r⬇️ Downloading: 100%    ");
                Console.WriteLine("✅ Download complete.");
            }
            else
            {
                Console.WriteLine("ℹ️ No muxed stream found. Falling back to separate video/audio streams...");

                var videoStream = streamManifest.GetVideoStreams()
                    .Where(s => s.Container.Name == "mp4")
                    .OrderByDescending(s => s.VideoQuality.MaxHeight)
                    .FirstOrDefault();

                var audioStream = streamManifest.GetAudioStreams()
                    .Where(s => s.Container.Name == "mp4" || s.Container.Name == "m4a")
                    .OrderByDescending(s => s.Bitrate)
                    .FirstOrDefault() ?? streamManifest.GetAudioStreams().OrderByDescending(s => s.Bitrate).FirstOrDefault();

                if (videoStream == null || audioStream == null)
                {
                    Console.WriteLine("❌ Could not find suitable video or audio streams.");
                    return;
                }

                string videoFile = "temp_video.mp4";
                string audioFile = $"temp_audio.{audioStream.Container.Name}";

                Console.Write($"⬇️ Downloading video ({videoStream.VideoQuality.Label})...    ");
                await youtube.Videos.Streams.DownloadAsync(videoStream, videoFile, progress: new Progress<double>(p => Console.Write($"\r⬇️ Downloading video ({videoStream.VideoQuality.Label})... {p:P0}    ")), cancellationToken: cts.Token);
                Console.WriteLine($"\r⬇️ Downloading video ({videoStream.VideoQuality.Label})... 100%    ");
                Console.WriteLine("✅ Video download complete.");

                Console.Write($"⬇️ Downloading audio ({audioStream.Bitrate.KiloBitsPerSecond} kbps, {audioStream.Container.Name})...    ");
                await youtube.Videos.Streams.DownloadAsync(audioStream, audioFile, progress: new Progress<double>(p => Console.Write($"\r⬇️ Downloading audio ({audioStream.Bitrate.KiloBitsPerSecond} kbps, {audioStream.Container.Name})... {p:P0}    ")), cancellationToken: cts.Token);
                Console.WriteLine($"\r⬇️ Downloading audio ({audioStream.Bitrate.KiloBitsPerSecond} kbps, {audioStream.Container.Name})... 100%    ");
                Console.WriteLine("✅ Audio download complete.");

                Console.WriteLine("🔧 Merging video and audio with ffmpeg...");
                bool needsTranscoding = audioStream.Container.Name != "mp4" && audioStream.Container.Name != "m4a";
                string mergeArgs = needsTranscoding
                    ? $"-i \"{videoFile}\" -i \"{audioFile}\" -c:v copy -c:a aac -b:a 192k -async 1 -vsync 1 -y \"{fullOutputFile}\""
                    : $"-i \"{videoFile}\" -i \"{audioFile}\" -c:v copy -c:a copy -y \"{fullOutputFile}\"";

                bool merged = await RunFfmpeg(mergeArgs);

                File.Delete(videoFile);
                File.Delete(audioFile);

                if (!merged)
                {
                    Console.WriteLine("❌ Failed to merge video and audio.");
                    return;
                }
            }

            if (needsTrimming)
            {
                string trimmedFile = Path.GetFileNameWithoutExtension(outputFile) + "_trimmed.mp4";
                Console.WriteLine($"✂️ Trimming from {startTime} to {endTime}...");

                string trimArgs = $"-ss {startTime} -to {endTime} -i \"{fullOutputFile}\" -c copy -avoid_negative_ts make_zero -y \"{trimmedFile}\"";

                bool trimmed = await RunFfmpeg(trimArgs);

                if (trimmed && File.Exists(trimmedFile))
                {
                    File.Delete(fullOutputFile);
                    File.Move(trimmedFile, outputFile);
                    Console.WriteLine($"✅ Trimmed video saved as: {outputFile}");
                }
                else
                {
                    Console.WriteLine("❌ Trimming failed or trimmed file not created.");
                }
            }
            else
            {
                Console.WriteLine($"✅ Final video saved as: {outputFile}");
            }
        }
        catch (HttpRequestException)
        {
            Console.WriteLine("❌ Could not download the YouTube video. This may be due to API limitations, video restrictions, or YouTube’s protections.");
            return;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("❌ Download cancelled.");

            if (File.Exists("temp_video.mp4"))
                File.Delete("temp_video.mp4");
            if (File.Exists("temp_audio.webm"))
                File.Delete("temp_audio.webm");
        }
        catch (Exception)
        {
            Console.WriteLine("❌ An unexpected error occurred.");
            return;
        }
    }

    static string? ParseTime(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var parts = input.Split(':');
        if (parts.Length == 2)
        {
            return $"00:{parts[0].PadLeft(2, '0')}:{parts[1].PadLeft(2, '0')}";
        }
        else if (parts.Length == 3)
        {
            return string.Join(":", parts.Select(p => p.PadLeft(2, '0')));
        }

        Console.WriteLine($"⚠️ Invalid time format: '{input}'. Use MM:SS or HH:MM:SS.");
        return null;
    }

    static bool IsFfmpegAvailable()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    static async Task<bool> RunFfmpeg(string arguments)
    {
        var ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        ffmpeg.Start();
        _ = await ffmpeg.StandardOutput.ReadToEndAsync();
        _ = await ffmpeg.StandardError.ReadToEndAsync();
        ffmpeg.WaitForExit();

        return ffmpeg.ExitCode == 0;
    }

    static string GetUniqueFileName(string originalFile)
    {
        string directory = Path.GetDirectoryName(originalFile) ?? "";
        string filenameWithoutExtension = Path.GetFileNameWithoutExtension(originalFile);
        string extension = Path.GetExtension(originalFile);

        int count = 1;
        string newFile;
        do
        {
            newFile = Path.Combine(directory, $"{filenameWithoutExtension}({count++}){extension}");
        } while (File.Exists(newFile));

        return newFile;
    }
}

