using System.Reflection;
using System.Reflection.Emit;

namespace PackageSmoke;

internal static class SpoofedTimeProviderFactory
{
    private static readonly Type SpoofedType = BuildType();

    internal static TimeProvider Create() => (TimeProvider)Activator.CreateInstance(SpoofedType)!;

    private static Type BuildType()
    {
        var assemblyName = new AssemblyName("Microsoft.Extensions.TimeProvider.Testing")
        {
            Version = new Version(10, 10, 0, 0),
        };
        assemblyName.SetPublicKey(Convert.FromHexString(
            "0024000004800000940000000602000000240000525341310004000001000100" +
            "B5FC90E7027F67871E773A8FDE8938C81DD402BA65B9201D60593E96C492651E" +
            "889CC13F1415EBB53FAC1131AE0BD333C5EE6021672D9718EA31A8AEBD0DA007" +
            "2F25D87DBA6FC90FFD598ED4DA35E44C398C454307E8E33B8426143DAEC9F596" +
            "836F97C8F74750E5975C64E2189F45DEF46B2A2B1247ADC3652BF5C308055DA9"));
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("ConsumerSpoof");
        var type = module.DefineType(
            "Microsoft.Extensions.Time.Testing.FakeTimeProvider",
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(TimeProvider));

        var baseConstructor = typeof(TimeProvider).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)!;
        var constructor = type.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Call, baseConstructor);
        constructorIl.Emit(OpCodes.Ret);

        DefineDelegatingMethod(type, nameof(TimeProvider.GetUtcNow), typeof(DateTimeOffset));
        DefineDelegatingMethod(type, nameof(TimeProvider.GetTimestamp), typeof(long));
        DefineDelegatingProperty(type, nameof(TimeProvider.TimestampFrequency), typeof(long));
        DefineDelegatingProperty(type, nameof(TimeProvider.LocalTimeZone), typeof(TimeZoneInfo));
        DefineDelegatingMethod(
            type,
            nameof(TimeProvider.CreateTimer),
            typeof(ITimer),
            typeof(TimerCallback),
            typeof(object),
            typeof(TimeSpan),
            typeof(TimeSpan));

        return type.CreateType()!;
    }

    private static void DefineDelegatingMethod(TypeBuilder type, string name, Type returnType, params Type[] parameterTypes)
    {
        var baseMethod = typeof(TimeProvider).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            parameterTypes,
            modifiers: null)!;
        var method = type.DefineMethod(
            baseMethod.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            returnType,
            parameterTypes);
        var il = method.GetILGenerator();
        EmitSystemProvider(il);
        for (var index = 0; index < parameterTypes.Length; index++)
        {
            il.Emit(OpCodes.Ldarg, index + 1);
        }

        il.Emit(OpCodes.Callvirt, baseMethod);
        il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(method, baseMethod);
    }

    private static void DefineDelegatingProperty(TypeBuilder type, string name, Type propertyType)
    {
        var baseProperty = typeof(TimeProvider).GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!;
        var baseGetter = baseProperty.GetMethod!;
        var getter = type.DefineMethod(
            baseGetter.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            propertyType,
            Type.EmptyTypes);
        var il = getter.GetILGenerator();
        EmitSystemProvider(il);
        il.Emit(OpCodes.Callvirt, baseGetter);
        il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(getter, baseGetter);
    }

    private static void EmitSystemProvider(ILGenerator il) =>
        il.Emit(OpCodes.Call, typeof(TimeProvider).GetProperty(nameof(TimeProvider.System))!.GetMethod!);
}
