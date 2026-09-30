namespace Hypa.Cli.Attach.Onboarding;

public enum OnboardingHitKind
{
    Continue = 0,
    Ignore,
    Outside,
}

public sealed record OnboardingHit(OnboardingHitKind Kind);

public static class OnboardingHitTest
{
    public static OnboardingHit Hit(OnboardingLayout? layout, int col, int row)
    {
        if (layout is null)
            return new OnboardingHit(OnboardingHitKind.Outside);
        if (layout.Continue.Contains(col, row))
            return new OnboardingHit(OnboardingHitKind.Continue);
        if (!layout.Panel.Contains(col, row))
            return new OnboardingHit(OnboardingHitKind.Outside);
        return new OnboardingHit(OnboardingHitKind.Ignore);
    }
}
