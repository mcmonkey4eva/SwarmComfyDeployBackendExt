using SwarmUI.Backends;
using SwarmUI.Core;

namespace SwarmComfyDeployBackendExt;

public class SwarmComfyDeployBackendExtension : Extension
{
    public static BackendHandler.BackendType BackType;

    public override void OnInit()
    {
        BackType = Program.Backends.RegisterBackendType<ComfyDeployBackend>("comfy_deploy_api", "ComfyDeploy", "ComfyDeploy.com API backend.", true, true);
    }
}
