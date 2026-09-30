using System;

namespace Corpus;

public interface IWorker
{
    void Run();
}

public class BaseWorker
{
    public virtual void Run()
    {
    }
}
