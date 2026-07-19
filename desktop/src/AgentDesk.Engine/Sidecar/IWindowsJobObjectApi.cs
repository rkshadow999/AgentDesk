using System.Runtime.InteropServices;

namespace AgentDesk.Engine.Sidecar;

internal interface IWindowsJobObjectApi
{
    SafeHandle CreateJobObject();

    void ConfigureKillOnClose(SafeHandle jobObject);

    void AssignProcess(SafeHandle jobObject, SafeHandle processHandle, int processId);
}
