namespace Hypa.Cli.Attach;

internal interface IEndpointRegistryPort
{
    string ActiveId { get; }
    bool SurfaceActive(string endpointId);
    bool SupportsSurfaceInterest(string endpointId);
    EndpointSendOutcome SendTo(string endpointId, EndpointActivationMessage message);
    void Fail(string endpointId, string error);
    void SetSurfaceActive(string endpointId, bool active);
    bool SetActive(string endpointId);
    void FreezeInput();
    void UnfreezeInput();
}
