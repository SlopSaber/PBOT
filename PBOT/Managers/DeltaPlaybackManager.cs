using PBOT.Models;
using PBOT.Services;
using SiraUtil.Zenject;
using System.Threading.Tasks;
using System.Threading;
using Zenject;
using System.Collections.Generic;
using System;

namespace PBOT.Managers;

internal class DeltaPlaybackManager : IDeltaPlaybackService, IAsyncInitializable, ITickable, IDisposable
{
    private readonly ScoreContract _scoreContract;
    private readonly IAudioTimeSource _audioTimeSource;
    private readonly IMultiplexedDeltaService _multiplexedDeltaService;

    private int _nextFrame;
    private IReadOnlyList<DeltaFrame>? _frames;
    private bool _disposed;
    private int _initializationRevision;

    public event Action<DeltaFrame>? OnFrameUpdated;

    public DeltaPlaybackManager(ScoreContract scoreContract, IAudioTimeSource audioTimeSource, IMultiplexedDeltaService multiplexedDeltaService)
    {
        _scoreContract = scoreContract;
        _audioTimeSource = audioTimeSource;
        _multiplexedDeltaService = multiplexedDeltaService;
    }


    public async Task InitializeAsync(CancellationToken token)
    {
        if (_disposed)
            return;
        int revision = ++_initializationRevision;
        var frames = await _multiplexedDeltaService.GetFramesAsync(_scoreContract, token);

        if (_disposed || token.IsCancellationRequested || revision != _initializationRevision || frames.Count is 0)
            return;

        _nextFrame = 0;
        _frames = frames;
    }

    public void Tick()
    {
        // Don't update if we don't have any frames or we've processed all of them.
        if (_disposed || _frames is null || _nextFrame >= _frames.Count)
            return;

        var now = _audioTimeSource.songTime;
        if (_frames[_nextFrame].Time > now)
            return;

        // Consume each due frame once, publishing only the latest one.
        DeltaFrame frame = _frames[_nextFrame++];
        while (_nextFrame < _frames.Count && _frames[_nextFrame].Time <= now)
        {
            frame = _frames[_nextFrame++];
        }

        // Send frame update
        OnFrameUpdated?.Invoke(frame);
    }

    public void Dispose()
    {
        _disposed = true;
        ++_initializationRevision;
        _frames = null;
        OnFrameUpdated = null;
    }
}
