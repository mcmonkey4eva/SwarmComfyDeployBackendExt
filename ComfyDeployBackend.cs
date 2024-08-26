using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.Net.Http;

namespace SwarmComfyDeployBackendExt;

public class ComfyDeployBackend : AbstractT2IBackend
{
    public class ComfyDeployBackendSettings : AutoConfiguration
    {
        [ConfigComment("Authorization token (not including the 'Bearer' prefix) for ComfyDeploy API.")]
        [ValueIsSecret]
        public string Authorization = "";

        [ConfigComment("Workflow ID provided by ComfyDeploy.\nMust be a workflow that accepts a generic 'workflow_api' JSON blob input.")]
        public string WorkflowID = "";

        [ConfigComment("Machine ID provided by ComfyDeploy.")]
        public string MachineID = "";

        [ConfigComment("API endpoint to use for ComfyDeploy.\nGenerally, do not change this.")]
        public string APIEndPoint = "https://www.comfydeploy.com/api/run";

        [ConfigComment("Max concurrent calls.")]
        public int OverQueue = 2;
    }

    public ComfyDeployBackendSettings Settings => SettingsRaw as ComfyDeployBackendSettings;

    public HashSet<string> Features = ["comfyui", "refiners", "controlnet", "endstepsearly", "seamless", "video", "variation_seed", "freeu", "yolov8"];

    public override IEnumerable<string> SupportedFeatures => Features;

    public override async Task Init()
    {
        // TODO: Validate API connection?
        MaxUsages = 1 + Settings.OverQueue;
        Status = BackendStatus.RUNNING;
    }

    public override async Task GenerateLive(T2IParamInput user_input, string batchId, Action<object> takeOutput)
    {
        string workflow = ComfyUIAPIAbstractBackend.CreateWorkflow(user_input, w => w, "/", Features);
        JObject payload = new()
        {
            ["workflow_id"] = Settings.WorkflowID,
            ["machine_id"] = Settings.MachineID,
            ["workflow_api"] = JObject.Parse(workflow)
        };
        Logs.Verbose($"[ComfyDeploy] send request: {payload.ToDenseDebugString()}");
        JObject response = await Send(HttpMethod.Post, Settings.APIEndPoint, payload);
        string runId = $"{response["run_id"]}";
        int ticks = 0;
        string lastStatus = "";
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5), Program.GlobalProgramCancel);
            try
            {
                JObject getResult = await Send(HttpMethod.Get, $"https://www.comfydeploy.com/api/run?run_id={runId}");
                Logs.Debug($"[ComfyDeploy] Response: {getResult?.ToDenseDebugString()}");
                string status = $"{getResult["status"]}";
                if (status != lastStatus || ticks++ == 20)
                {
                    Logs.Verbose($"[ComfyDeploy] Status: {status}");
                    lastStatus = status;
                    ticks = 0;
                }
                if (getResult["ended_at"].Type == JTokenType.Null)
                {
                    continue;
                }
                List<Task> downloads = [];
                foreach (JToken output in getResult["outputs"])
                {
                    foreach (JToken img in output["data"]["images"])
                    {
                        string fname = $"{img["filename"]}";
                        string ext = fname.AfterLast('.');
                        string url = $"{img["url"]}";
                        downloads.Add(Task.Run(async () =>
                        {
                            HttpResponseMessage response = await Utilities.UtilWebClient.GetAsync(url);
                            response.EnsureSuccessStatusCode();
                            byte[] data = await response.Content.ReadAsByteArrayAsync();
                            takeOutput(new Image(data, Image.ImageType.IMAGE, string.IsNullOrWhiteSpace(ext) ? "png" : ext));
                        }));
                    }
                }
                if (downloads.Any())
                {
                    await Task.WhenAll(downloads);
                }
                break;
            }
            catch (Exception ex)
            {
                Logs.Error($"[ComfyDeploy] Error: {ex}");
                throw;
            }
        }
    }

    public override async Task<Image[]> Generate(T2IParamInput user_input)
    {
        List<Image> images = [];
        await GenerateLive(user_input, "0", output =>
        {
            if (output is Image img)
            {
                images.Add(img);
            }
        });
        return [.. images];
    }

    public override async Task<bool> LoadModel(T2IModel model)
    {
        // TODO: Validate model is on server?
        CurrentModelName = model.Name;
        return true;
    }

    public async Task<JObject> Send(HttpMethod method, string endpoint, JObject payload = null)
    {
        HttpRequestMessage request = new(method, endpoint);
        request.Headers.Authorization = new("Bearer", Settings.Authorization);
        if (payload is not null)
        {
            request.Content = Utilities.JSONContent(payload);
        }
        return await NetworkBackendUtils.Parse<JObject>(await Utilities.UtilWebClient.SendAsync(request));
    }

    public override async Task Shutdown()
    {
        // TODO
        if (Status == BackendStatus.RUNNING)
        {
            Status = BackendStatus.DISABLED;
        }
    }
}
