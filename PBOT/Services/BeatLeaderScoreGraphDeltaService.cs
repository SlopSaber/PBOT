using Newtonsoft.Json;
using PBOT.Models;
using SiraUtil.Logging;
using SiraUtil.Web;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PBOT.Services;

internal class BeatLeaderScoreGraphDeltaService : IDeltaService
{
    private readonly SiraLog _siraLog;
    private readonly IHttpService _httpService;
    private readonly OculusStudios.Platform.Core.IPlatform _platformUserModel;
    private const string _beatLeaderApiUrl = "https://api.beatleader.xyz";
    private CachedContractId? _cached;
    private long _metadataRevision;
    private readonly HashSet<Task> _preparations = new();

    private record struct CachedContractId(int Id, ScoreContract Contract);
    private record struct ScoreGraphTracker([property: JsonProperty("graph")] float[] Graph);
    private record struct BeatLeaderScoreStatistics([property: JsonProperty("scoreGraphTracker")] ScoreGraphTracker Tracker);
    private record struct BeatLeaderScore([property: JsonProperty("id")] int Id, [property: JsonProperty("modifiedScore")] int TotalScore, [property: JsonProperty("timeset")] string TimeSet); // Why is the timestamp a string?
    private class BeatLeaderMetadata : DeltaMetadata { [JsonIgnore] public int Id { get; set; } }
    private record struct MetadataRequest(string Json, CultureInfo Culture);

    public BeatLeaderScoreGraphDeltaService(SiraLog siraLog, IHttpService httpService, OculusStudios.Platform.Core.IPlatform platformUserModel)
    {
        _siraLog = siraLog;
        _httpService = httpService;
        _platformUserModel = platformUserModel;
    }

    public async Task<IReadOnlyList<DeltaFrame>> GetFramesAsync(ScoreContract contract, CancellationToken cancellationToken = default)
    {
        _siraLog.Debug($"Fetching delta frames for {contract}");
        int? id = _cached?.Contract == contract ? _cached.Value.Id : null;
        if (_cached?.Contract != contract)
        {
            var metadata = await GetMetadataAsync(contract, cancellationToken);
            if (metadata is BeatLeaderMetadata beatLeaderMetadata)
                id = beatLeaderMetadata.Id;
        }

        if (id is null)
        {
            _siraLog.Debug($"Could not load delta frames for {contract}");
            return Array.Empty<DeltaFrame>();
        }

        _siraLog.Debug("Downloading statistics");
        var url = $"{_beatLeaderApiUrl}/score/statistic/{id.Value}";
        var response = await _httpService.GetAsync(url, cancellationToken: cancellationToken);
        if (!response.Successful)
        {
            _siraLog.Warn($"Could not download score statistic data for {contract}");
            return Array.Empty<DeltaFrame>();
        }


        _siraLog.Debug("Reading statistics response body");
        var data = await response.ReadAsStringAsync();
        if (JsonConvert.DefaultSettings != null)
        {
            // Custom converters retain their caller contract; capture their result before worker projection.
            var graph = JsonConvert.DeserializeObject<BeatLeaderScoreStatistics>(data).Tracker.Graph;
            return await PrepareAsync(ProjectGraph, (float[])graph.Clone(), cancellationToken);
        }
        return await PrepareAsync(ParseGraph, data, cancellationToken);
    }

    public async Task<DeltaMetadata?> GetMetadataAsync(ScoreContract contract, CancellationToken cancellationToken = default)
    {
        long revision = ++_metadataRevision;
        var (hash, mode, difficulty) = contract;

        _siraLog.Debug($"Loading metadata for {contract}");
        string userId = _platformUserModel.user.userId.ToString();
        var url = $"{_beatLeaderApiUrl}/score/{userId}/{hash}/{difficulty.SerializedName()}/{mode}";
        var response = await _httpService.GetAsync(url, cancellationToken: cancellationToken);
        if (!response.Successful)
        {
            _siraLog.Debug($"Could not find score for {contract}");
            return null;
        }

        _siraLog.Debug("Reading response body");
        var data = await response.ReadAsStringAsync();
        BeatLeaderMetadata metadata;
        if (JsonConvert.DefaultSettings != null)
        {
            var score = JsonConvert.DeserializeObject<BeatLeaderScore>(data);
            metadata = CreateMetadata(score, CultureInfo.CurrentCulture);
        }
        else
        {
            CultureInfo culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
            metadata = await PrepareAsync(ParseMetadata, new MetadataRequest(data, culture), cancellationToken);
        }

        _siraLog.Debug("Caching replay url");
        if (_metadataRevision == revision)
            _cached = new CachedContractId(metadata.Id, contract);

        _siraLog.Debug("Generating metadata");
        return metadata;
    }

    private async Task<T> PrepareAsync<T>(Func<object?, T> prepare, object state, CancellationToken token)
    {
        Task<T> worker = Task.Factory.StartNew(prepare, state, token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        _preparations.Add(worker);
        try
        {
            return await worker;
        }
        finally
        {
            _preparations.Remove(worker);
        }
    }

    private static List<DeltaFrame> ParseGraph(object? state)
        => ProjectGraph(DeserializeDefault<BeatLeaderScoreStatistics>((string)state!).Tracker.Graph);

    private static List<DeltaFrame> ProjectGraph(object? state)
    {
        var graph = (float[])state!;
        List<DeltaFrame> frames = new(graph.Length + 1) { new DeltaFrame { Time = 0f, Current = 1f } };
        float second = 1f;
        foreach (float accuracy in graph)
            frames.Add(new DeltaFrame { Time = second++, Current = accuracy });
        return frames;
    }

    private static BeatLeaderMetadata ParseMetadata(object? state)
    {
        var request = (MetadataRequest)state!;
        return CreateMetadata(DeserializeDefault<BeatLeaderScore>(request.Json), request.Culture);
    }

    private static BeatLeaderMetadata CreateMetadata(BeatLeaderScore score, CultureInfo culture)
        => new()
        {
            Id = score.Id,
            Source = "BeatLeader Score Graph",
            TotalScore = score.TotalScore,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(score.TimeSet, NumberStyles.Integer, culture)),
        };

    private static T DeserializeDefault<T>(string json)
    {
        // Capture default behavior without invoking a later replacement global settings factory on the worker.
        JsonSerializer serializer = JsonSerializer.Create();
        serializer.CheckAdditionalContent = true;
        using var reader = new JsonTextReader(new StringReader(json));
        return serializer.Deserialize<T>(reader)!;
    }

    public Task SaveAsync(ScoreContract score, DeltaMetadata metadata, List<DeltaFrame> frames, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
