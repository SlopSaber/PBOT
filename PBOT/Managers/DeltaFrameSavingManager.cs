using PBOT.Models;
using PBOT.Services;
using SiraUtil.Logging;
using SiraUtil.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Zenject;

namespace PBOT.Managers;

internal class DeltaFrameSavingManager : IInitializable, IDisposable
{
    private readonly ILevelFinisher _levelFinisher;
    private readonly IFrameContainerService _frameContainerService;
    private readonly FileSystemDeltaService _fileSystemDeltaService;
    private readonly SiraLog _siraLog;
    private readonly HashSet<Task> _pendingSaves = new();

    public DeltaFrameSavingManager(ILevelFinisher levelFinisher, IFrameContainerService frameContainerService, FileSystemDeltaService fileSystemDeltaService, SiraLog siraLog)
    {
        _levelFinisher = levelFinisher;
        _frameContainerService = frameContainerService;
        _fileSystemDeltaService = fileSystemDeltaService;
        _siraLog = siraLog;
    }

    public void Initialize()
    {
        _levelFinisher.StandardLevelDidFinish += LevelFinisher_StandardLevelDidFinish;
        _levelFinisher.MissionLevelDidFinish += LevelFinisher_MissionLevelDidFinish;
    }

    private void LevelFinisher_StandardLevelDidFinish(StandardLevelScenesTransitionSetupData sceneSetup, LevelCompletionResults lcr)
    {
        if (sceneSetup.beatmapLevel is { } beatmap)
            Save(beatmap, sceneSetup.beatmapKey, lcr);
    }

    private void LevelFinisher_MissionLevelDidFinish(MissionLevelScenesTransitionSetupData sceneSetup, MissionCompletionResults mlcr)
    {
        // Mission levels do not have a custom BeatmapLevel contract to save.
    }

    private void Save(BeatmapLevel beatmap, BeatmapKey beatmapKey, LevelCompletionResults results)
    {
        var frames = _frameContainerService.Frames;
        _frameContainerService.Frames = null; // Reset the frame reference: we don't need it anymore.

        // Only save the frame data if we have frames and the level has cleared
        if (frames is null || results.levelEndStateType is not LevelCompletionResults.LevelEndStateType.Cleared)
            return;

        // Pull necessary info to generate the contract and metadata
        var mode = beatmapKey.characteristic.SerializedName();
        var level = beatmap.levelID.Replace("custom_level_", string.Empty);
        var score = results.multipliedScore;
        var diff = beatmapKey.difficulty;

        ScoreContract contract = new(level, mode, diff);
        Task pending = _fileSystemDeltaService.SaveIfBetterAsync(contract, score, frames);
        _pendingSaves.Add(pending);
        ObserveSave(pending);
    }

    private async void ObserveSave(Task pending)
    {
        try
        {
            await pending;
        }
        catch (Exception error)
        {
            _siraLog.Error(error);
        }
        finally
        {
            _pendingSaves.Remove(pending);
        }
    }

    public void Dispose()
    {
        _levelFinisher.MissionLevelDidFinish -= LevelFinisher_MissionLevelDidFinish;
        _levelFinisher.StandardLevelDidFinish -= LevelFinisher_StandardLevelDidFinish;
    }
}
