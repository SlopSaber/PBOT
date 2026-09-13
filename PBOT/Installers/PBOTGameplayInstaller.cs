using IPA.Loader;
using PBOT.Managers;
using PBOT.Models;
using Zenject;

namespace PBOT.Installers;

internal class PBOTGameplayInstaller : Installer
{
    public override void InstallBindings()
    {
        Container.BindInterfacesTo<DeltaRecordingManager>().AsSingle();
        Container.BindInterfacesTo<DeltaPlaybackManager>().AsSingle();

        Container.Bind<ScoreContract>().FromMethod(Context =>
        {
            var setup = Context.Container.Resolve<GameplayCoreSceneSetupData>();
            var mode = setup.beatmapKey.characteristic.SerializedName();
            var level = setup.beatmapLevel.levelID.Replace("custom_level_", string.Empty);
            return new ScoreContract(level, mode, setup.beatmapKey.difficulty);
        }).AsSingle();
    }
}
