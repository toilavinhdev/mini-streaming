using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using Xabe.FFmpeg.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer().AddSwaggerGen();

var app = builder.Build();

app.UseSwagger().UseSwaggerUI();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")),
    RequestPath = ""
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var executablesPath = app.Environment.ContentRootPath;

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        executablesPath = Path.Combine(executablesPath, "FFmpeg", "windows");
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        executablesPath = Path.Combine(executablesPath, "FFmpeg", "linux");
    }

    if (!Directory.Exists(executablesPath)) Directory.CreateDirectory(executablesPath);

    FFmpeg.SetExecutablesPath(executablesPath);

    if (!File.Exists(Path.Combine(executablesPath, "ffmpeg.exe")) &&
        !File.Exists(Path.Combine(executablesPath, "ffmpeg")))
    {
        app.Logger.LogInformation("Starting download FFmpeg: {OS}", RuntimeInformation.OSDescription);
        FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, executablesPath).GetAwaiter().GetResult();
        app.Logger.LogInformation("Finished download FFmpeg");
    }
});

app.MapPost("api/video/upload", async (IFormFile file, IWebHostEnvironment environment) =>
{
    var processId = Guid.NewGuid().ToString("N");
    var processFolder = Path.Combine(environment.ContentRootPath, "FFmpeg", "temp", processId);
    if (!Directory.Exists(processFolder)) Directory.CreateDirectory(processFolder);
    var conversionFolder = Path.Combine(environment.ContentRootPath, "FFmpeg", "conversions", processId);
    if (!Directory.Exists(conversionFolder)) Directory.CreateDirectory(conversionFolder);
    
    var savedPath = Path.Combine(processFolder, file.FileName);
    await using (Stream fileStream = new FileStream(savedPath, FileMode.Create))
    {
        await file.CopyToAsync(fileStream);
    }

    var mediaInfo = await FFmpeg.GetMediaInfo(savedPath);
    var height = mediaInfo.VideoStreams.FirstOrDefault()?.Height ?? 0;

    var outputHeights = new List<int>();
    if (height >= 144) outputHeights.Add(144);
    if (height >= 240) outputHeights.Add(240);
    if (height >= 360) outputHeights.Add(360);
    if (height >= 480) outputHeights.Add(480);
    if (height >= 720) outputHeights.Add(720);
    if (height >= 1080) outputHeights.Add(1080);

    var conversion = FFmpeg.Conversions.New();
    conversion.AddParameter($"-hide_banner -i \"{savedPath}\"");
    conversion.SetOverwriteOutput(true);

    foreach (var outputHeight in outputHeights)
    {
        var (w, bv, maxRate, bufSize, v) = outputHeight switch
        {
            144 => ("256", "200k", "214k", "300k", "0"),
            240 => ("426", "400k", "428k", "600k", "0"),
            360 => ("640", "800k", "856k", "1200k", "0"),
            480 => ("842", "1400k", "1498k", "2100k", "1"),
            720 => ("1280", "2800k", "2996k", "4200k", "2"),
            1080 => ("1920", "5000k", "5350k", "7500k", "3"),
            _ => throw new NotImplementedException()
        };
        conversion.AddParameter("-c:a aac");
        conversion.AddParameter("-ar 48000");
        conversion.AddParameter("-c:v h264");
        conversion.AddParameter("-profile:v main");
        conversion.AddParameter("-crf 19");
        conversion.AddParameter("-sc_threshold 0");
        conversion.AddParameter("-g 60");
        conversion.AddParameter("-keyint_min 60");
        conversion.AddParameter("-hls_time 1");
        conversion.AddParameter("-hls_playlist_type vod");
        conversion.AddParameter($"-vf scale=w={w}:h=-2");
        conversion.AddParameter($"-b:v {bv}");
        conversion.AddParameter($"-maxrate {maxRate}");
        conversion.AddParameter($"-bufsize {bufSize}");
        conversion.AddParameter("-b:a 0k");
        conversion.AddParameter($"-hls_segment_filename \"{Path.Combine(conversionFolder, $"{outputHeight}p_%03d.ts")}\" \"{Path.Combine(conversionFolder, $"{outputHeight}p.m3u8")}\"");
    }
    
    ConversionProgressEventArgs? argsSummary = null;
    conversion.OnProgress += (sender, args) =>
    {
        argsSummary = args;
        var percent = (int)(Math.Round(args.Duration.TotalSeconds / args.TotalLength.TotalSeconds, 2) * 100);
        app.Logger.LogInformation("{Prefix}: [{Duration} / {TotalLength}] {Percent}%",
            processId, args.Duration, args.TotalLength, percent);
    };

    app.Logger.LogInformation("ffmpeg{Args}", conversion.Build());

    var stopwatch = new Stopwatch();
    stopwatch.Start();
    await conversion.Start();
    stopwatch.Stop();
    
    app.Logger.LogInformation("{Prefix}: [{Duration} / {TotalLength}] {Percent}% in {ElapsedTime}",
        processId, argsSummary?.TotalLength, argsSummary?.TotalLength, 100, stopwatch.Elapsed.ToString("g"));

    return TypedResults.Ok(processId);
})
.DisableAntiforgery();

app.MapGet("api/video/streaming/{processId}/{fileName}", (string processId, string fileName, IWebHostEnvironment environment) =>
{
    var processFolder = Path.Combine(environment.ContentRootPath, "FFmpeg", "conversions", processId);
    var videoPath = Path.Combine(processFolder, fileName);
    return TypedResults.PhysicalFile(videoPath, "application/x-mpegURL");
});

app.Run();