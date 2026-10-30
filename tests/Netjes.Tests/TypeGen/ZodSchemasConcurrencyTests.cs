using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Netjes.TypeGen;

namespace Netjes.Tests.TypeGen;

public class ZodSchemasConcurrencyTests
{
    // NullabilityInfoContext keeps unsynchronised dictionaries, so two generations running at once (startup generation
    // plus a command, or parallel test classes) must not share one. Every round releases all threads together onto a
    // type nothing has read yet, so its cache entries are still cold. The type carries many attributes to widen the
    // window between the context's lookup and its add.
    [Fact]
    public void ConcurrentGenerations_DoNotRaceOnNullabilityCaches()
    {
        const int rounds = 300;
        const int threads = 16;

        var types = CreateTypes(rounds);
        using var barrier = new Barrier(threads);
        var current = 0;
        var errors = new List<Exception>();
        var results = new string[threads];

        // Dedicated background threads: a corrupted dictionary can spin forever, and that must not starve the pool.
        var workers = Enumerable.Range(0, threads).Select(thread => new Thread(() =>
        {
            try
            {
                for (var round = 0; round < rounds; round++)
                {
                    barrier.SignalAndWait();
                    var schemas = new ZodSchemas(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    schemas.Add(types[round]);
                    results[thread] = schemas.Declarations();
                    barrier.SignalAndWait();
                    if (thread == 0)
                    {
                        Volatile.Write(ref current, round + 1);
                    }
                }
            }
            catch (Exception e)
            {
                lock (errors)
                {
                    errors.Add(e);
                }

                barrier.RemoveParticipant();
            }
        }) { IsBackground = true }).ToArray();

        foreach (var worker in workers)
        {
            worker.Start();
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        var hung = workers.Any(w => !w.Join(TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds))));

        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(3).Select(e => e.Message + " at " + string.Join(" <- ", (e.StackTrace ?? "").Split('\n').Skip(1).Take(4).Select(f => f.Trim())))));
        Assert.False(hung, "Generation hung: a shared cache was corrupted.");
        Assert.Equal(rounds, Volatile.Read(ref current));
    }

    private static Type[] CreateTypes(int count)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Race_{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("m");
        var nullable = typeof(NullableAttribute).GetConstructor([typeof(byte)])!;
        var context = typeof(NullableContextAttribute).GetConstructor([typeof(byte)])!;
        var filler = typeof(InternalsVisibleToAttribute).GetConstructor([typeof(string)])!;

        return Enumerable.Range(0, count).Select(i =>
        {
            var type = module.DefineType($"Race_{i}", TypeAttributes.Public | TypeAttributes.Class);
            type.SetCustomAttribute(new CustomAttributeBuilder(context, [(byte)1]));
            for (var a = 0; a < 40; a++)
            {
                type.SetCustomAttribute(new CustomAttributeBuilder(filler, [$"Filler{a}"]));
            }

            type.DefineDefaultConstructor(MethodAttributes.Public);
            for (var p = 0; p < 4; p++)
            {
                var field = type.DefineField($"_f{p}", typeof(string), FieldAttributes.Private);
                var property = type.DefineProperty($"P{p}", PropertyAttributes.None, typeof(string), null);
                var getter = type.DefineMethod($"get_P{p}", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(string), Type.EmptyTypes);
                var il = getter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, field);
                il.Emit(OpCodes.Ret);
                var setter = type.DefineMethod($"set_P{p}", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, [typeof(string)]);
                il = setter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, field);
                il.Emit(OpCodes.Ret);
                property.SetGetMethod(getter);
                property.SetSetMethod(setter);
                if (p % 2 == 1)
                {
                    property.SetCustomAttribute(new CustomAttributeBuilder(nullable, [(byte)2]));
                }
            }

            return type.CreateType();
        }).ToArray();
    }
}
