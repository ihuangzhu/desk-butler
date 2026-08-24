if (args.Length != 1)
{
    return 2;
}

var mutex = new Mutex(initiallyOwned: false, args[0]);
mutex.WaitOne();
Console.WriteLine("acquired");
Console.Out.Flush();
GC.KeepAlive(mutex);
return 0;
