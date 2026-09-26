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
    private static readonly string _storageDirectory = Path.Combine(UnityGame.UserDataPath, "PBOT", "Storage");

    public Task<IReadOnlyList<DeltaFrame>> GetFramesAsync(ScoreContract contract, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DeltaFrame>>(() =>
        {
            CreateDirectory();
            var file = Path.Combine(_storageDirectory, $"{contract}.deltaf");
            if (!File.Exists(file))
                return Array.Empty<DeltaFrame>();

            using var frameFileStream = File.OpenRead(file);
            return Serializer.Deserialize<List<DeltaFrame>>(frameFileStream);
        }, cancellationToken);
    }

    public Task<DeltaMetadata?> GetMetadataAsync(ScoreContract contract, CancellationToken cancellationToken = default)
    {
        return Task.Run<DeltaMetadata?>(() =>
        {
            CreateDirectory();
            var file = Path.Combine(_storageDirectory, $"{contract}.delta");
            if (!File.Exists(file))
                return null;

            var metadataString = File.ReadAllText(file);
            return JsonConvert.DeserializeObject<DeltaMetadata?>(metadataString);
        }, cancellationToken);
    }

    public Task SaveAsync(ScoreContract score, DeltaMetadata metadata, List<DeltaFrame> frames, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            CreateDirectory();
            File.WriteAllText(Path.Combine(_storageDirectory, $"{score}.delta"), JsonConvert.SerializeObject(metadata));
            using var frameFileStream = File.Create(Path.Combine(_storageDirectory, $"{score}.deltaf"));
            Serializer.Serialize(frameFileStream, frames);
        }, cancellationToken);
    }

    private static void CreateDirectory()
    {
        Directory.CreateDirectory(_storageDirectory);
    }
}
