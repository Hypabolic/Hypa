using System;

namespace Corpus;

public class Worker : BaseWorker, IWorker
{
    public override void Run()
    {
        Helper();
        Console.WriteLine("done");
    }

    private void Helper()
    {
    }
}
