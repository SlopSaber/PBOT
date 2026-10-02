using IPA.Utilities;
using Newtonsoft.Json;
using PBOT.Models;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PBOT.Services;

internal class FileSystemDeltaService : IDeltaService
{
    private enum Operation { ReadFrames, ReadMetadata, Save, SaveIfBetter }

    private sealed class Result
    {
        public IReadOnlyList<DeltaFrame> Frames = Array.Empty<DeltaFrame>();
        public DeltaMetadata? Metadata;
    }

    private sealed class Request
    {
        public readonly Operation Operation;
        public readonly string DirectoryPath;
        public readonly string MetadataPath;
        public readonly string FramesPath;
        public readonly DeltaMetadata? Metadata;
        public readonly List<DeltaFrame>? Frames;
        public readonly long TotalScore;
        public readonly CancellationToken CancellationToken;
        public readonly TaskCompletionSource<Result> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Request(Operation operation, string directoryPath, string metadataPath, string framesPath,
            DeltaMetadata? metadata, List<DeltaFrame>? frames, long totalScore, CancellationToken cancellationToken)
        {
            Operation = operation;
            DirectoryPath = directoryPath;
            MetadataPath = metadataPath;
            FramesPath = framesPath;
            Metadata = metadata;
            Frames = frames;
            TotalScore = totalScore;
            CancellationToken = cancellationToken;
        }
    }

    private static readonly string _storageDirectory;
    private static readonly object _gate = new();
    private static readonly Queue<Request> _requests = new();
    private static Task? _worker;

    static FileSystemDeltaService()
    {
        _storageDirectory = Path.GetFullPath(Path.Combine(UnityGame.UserDataPath, "PBOT", "Storage"));
    }

    public Task<IReadOnlyList<DeltaFrame>> GetFramesAsync(ScoreContract contract, CancellationToken cancellationToken = default)
    {
        return GetFramesResultAsync(Enqueue(Operation.ReadFrames, contract, null, null, 0, cancellationToken));
    }

    public Task<DeltaMetadata?> GetMetadataAsync(ScoreContract contract, CancellationToken cancellationToken = default)
    {
        return GetMetadataResultAsync(Enqueue(Operation.ReadMetadata, contract, null, null, 0, cancellationToken));
    }

    public Task SaveAsync(ScoreContract score, DeltaMetadata metadata, List<DeltaFrame> frames, CancellationToken cancellationToken = default)
    {
        return Enqueue(Operation.Save, score, metadata, frames, 0, cancellationToken);
    }

    public Task SaveIfBetterAsync(ScoreContract score, long totalScore, List<DeltaFrame> frames, CancellationToken cancellationToken = default)
    {
        return Enqueue(Operation.SaveIfBetter, score, null, frames, totalScore, cancellationToken);
    }

    private static async Task<IReadOnlyList<DeltaFrame>> GetFramesResultAsync(Task<Result> task) => (await task.ConfigureAwait(false)).Frames;
    private static async Task<DeltaMetadata?> GetMetadataResultAsync(Task<Result> task) => (await task.ConfigureAwait(false)).Metadata;

    private static Task<Result> Enqueue(Operation operation, ScoreContract score, DeltaMetadata? metadata,
        List<DeltaFrame>? frames, long totalScore, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<Result>(cancellationToken);

        Request request;
        try
        {
            string scoreName = score.ToString();
            request = new Request(operation, _storageDirectory,
                Path.GetFullPath(Path.Combine(_storageDirectory, $"{scoreName}.delta")),
                Path.GetFullPath(Path.Combine(_storageDirectory, $"{scoreName}.deltaf")),
                metadata, frames, totalScore, cancellationToken);
        }
        catch (Exception error)
        {
            return Task.FromException<Result>(error);
        }
        lock (_gate)
        {
            _requests.Enqueue(request);
            if (_worker == null)
                StartWorker();
        }
        return request.Completion.Task;
    }

    private static void StartWorker()
    {
        _worker = Task.Factory.StartNew(ProcessQueue, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        _worker.ContinueWith(Completed, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void ProcessQueue()
    {
        while (true)
        {
            Request request;
            lock (_gate)
            {
                if (_requests.Count == 0)
                    return;
                request = _requests.Dequeue();
            }

            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                continue;
            }

            try
            {
                request.Completion.SetResult(Execute(request));
            }
            catch (Exception error)
            {
                request.Completion.SetException(error);
            }
        }
    }

    private static void Completed(Task completed)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_worker, completed))
                return;
            // Release the slot only after the physical file worker has finished.
            _worker = null;
            if (_requests.Count > 0)
                StartWorker();
        }
    }

    private static Result Execute(Request request)
    {
        Directory.CreateDirectory(request.DirectoryPath);
        switch (request.Operation)
        {
            case Operation.ReadFrames:
                if (!File.Exists(request.FramesPath))
                    return new Result();
                using (var stream = File.OpenRead(request.FramesPath))
                    return new Result { Frames = Serializer.Deserialize<List<DeltaFrame>>(stream) };
            case Operation.ReadMetadata:
                return new Result { Metadata = ReadMetadata(request.MetadataPath) };
            case Operation.Save:
                Save(request.MetadataPath, request.FramesPath, request.Metadata!, request.Frames!);
                break;
            case Operation.SaveIfBetter:
                DeltaMetadata? previous = ReadMetadata(request.MetadataPath);
                if (previous != null && previous.TotalScore >= request.TotalScore)
                    break;
                var metadata = new DeltaMetadata
                {
                    Source = "Local",
                    Timestamp = DateTimeOffset.UtcNow.AddMinutes(2f),
                    Version = new Hive.Versioning.Version(1, 0, 0),
                    TotalScore = request.TotalScore,
                };
                Save(request.MetadataPath, request.FramesPath, metadata, request.Frames!);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Operation));
        }
        return new Result();
    }

    private static DeltaMetadata? ReadMetadata(string path)
    {
        if (!File.Exists(path))
            return null;
        return JsonConvert.DeserializeObject<DeltaMetadata?>(File.ReadAllText(path));
    }

    private static void Save(string metadataPath, string framesPath, DeltaMetadata metadata, List<DeltaFrame> frames)
    {
        File.WriteAllText(metadataPath, JsonConvert.SerializeObject(metadata));
        using var stream = File.Create(framesPath);
        Serializer.Serialize(stream, frames);
    }
}
