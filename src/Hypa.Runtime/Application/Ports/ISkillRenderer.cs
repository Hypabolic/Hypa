namespace Hypa.Runtime.Application.Ports;

public interface ISkillRenderer
{
    string Render(bool fullSections);
    string GetRulesContent();
}
