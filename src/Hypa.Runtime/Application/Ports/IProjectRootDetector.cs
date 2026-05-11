namespace Hypa.Runtime.Application.Ports;

public interface IProjectRootDetector
{
    string? Detect(string startPath);
}
