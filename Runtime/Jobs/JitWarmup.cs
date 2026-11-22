using System.Reflection;
using System.Runtime.CompilerServices;

namespace CodexEngine.Runtime;

/// <summary>
/// Compiles the methods of game assemblies ahead of their first call. A method's first call otherwise compiles it on
/// the calling thread: the first shot, death or spawn of a kind stalls the frame it happens in. Run it on a background
/// thread while a level loads. Generic methods over unknown arguments cannot be prepared and still compile on first use.
/// </summary>
public static class JitWarmup
{
    private static int started;

    /// <summary>Starts preparing the given assemblies once per process on a low-priority background thread.</summary>
    public static void Start(params Assembly[] assemblies)
    {
        if(Interlocked.Exchange(ref started,1)!=0)return;
        var thread=new Thread(()=>Prepare(assemblies)){IsBackground=true,Priority=ThreadPriority.BelowNormal,Name="JIT warm-up"};
        thread.Start();
    }

    /// <summary>Prepares every concrete, non-generic method and constructor. Returns how many were prepared.</summary>
    public static int Prepare(IEnumerable<Assembly> assemblies)
    {
        const BindingFlags all=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly;
        int prepared=0;
        foreach(var assembly in assemblies)
        {
            Type[] types;
            try{types=assembly.GetTypes();}
            catch(ReflectionTypeLoadException partial){types=partial.Types.Where(t=>t!=null).ToArray()!;}
            foreach(var type in types)
            {
                if(type.ContainsGenericParameters)continue;
                foreach(var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    if(method.IsAbstract || method.ContainsGenericParameters || (method.MethodImplementationFlags&MethodImplAttributes.InternalCall)!=0)continue;
                    try{RuntimeHelpers.PrepareMethod(method.MethodHandle);prepared++;}
                    catch(Exception error) when(error is ArgumentException or NotSupportedException or BadImageFormatException or TypeLoadException or InvalidOperationException){}
                }
            }
        }
        return prepared;
    }
}
