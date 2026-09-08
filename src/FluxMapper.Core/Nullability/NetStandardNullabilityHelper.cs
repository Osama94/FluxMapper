// netstandard2.0-only: System.Reflection.NullabilityInfoContext (used by the net10.0 build of
// NullabilityAnalysis) was added in .NET 6 and isn't available here. This reads the same underlying
// compiler-emitted signal NullabilityInfoContext itself reads -- the
// System.Runtime.CompilerServices.NullableAttribute/NullableContextAttribute byte flags the C# compiler
// writes for `#nullable enable` code -- narrowed to the single question NullabilityAnalysis actually asks
// (is this reference-typed member/parameter's own top-level annotation nullable?), rather than
// reimplementing NullabilityInfoContext's full graph API (which also answers questions about nested
// generic type-argument nullability that this codebase never asks).
//
// Flag values match the compiler's own encoding: 0 = Oblivious (no #nullable context; treated as
// not-nullable, same as NullabilityState.Unknown would be by the net10.0 branch's `== Nullable` check),
// 1 = NotAnnotated, 2 = Nullable.
#if NETSTANDARD2_0

using System.Collections.Generic;
using System.Reflection;

namespace FluxMapper.Core.Nullability;

internal static class NetStandardNullabilityHelper
{
    private const string NullableAttributeFullName = "System.Runtime.CompilerServices.NullableAttribute";
    private const string NullableContextAttributeFullName = "System.Runtime.CompilerServices.NullableContextAttribute";

    public static bool IsReferenceTypeNullable(MemberInfo member)
    {
        var flag = ReadNullableFlag(member.GetCustomAttributesData()) ?? ReadContextFlag(member.DeclaringType);
        return flag == 2;
    }

    public static bool IsReferenceTypeNullable(ParameterInfo parameter)
    {
        var flag = ReadNullableFlag(parameter.GetCustomAttributesData()) ?? ReadContextFlag(parameter.Member.DeclaringType);
        return flag == 2;
    }

    private static byte? ReadNullableFlag(IList<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName != NullableAttributeFullName) continue;
            if (attribute.ConstructorArguments.Count == 0) return null;

            var arg = attribute.ConstructorArguments[0];
            if (arg.ArgumentType == typeof(byte))
            {
                return (byte)arg.Value!;
            }

            if (arg.ArgumentType == typeof(byte[]) && arg.Value is IList<CustomAttributeTypedArgument> elements && elements.Count > 0)
            {
                // The first element is always the flag for the member's own top-level type reference;
                // later elements (if any) describe nested generic type arguments, which this method
                // deliberately doesn't need to answer.
                return (byte)elements[0].Value!;
            }

            return null;
        }

        return null;
    }

    private static byte? ReadContextFlag(Type? type)
    {
        while (type is not null)
        {
            foreach (var attribute in type.GetCustomAttributesData())
            {
                if (attribute.AttributeType.FullName != NullableContextAttributeFullName) continue;
                if (attribute.ConstructorArguments.Count > 0 && attribute.ConstructorArguments[0].ArgumentType == typeof(byte))
                {
                    return (byte)attribute.ConstructorArguments[0].Value!;
                }
            }

            type = type.DeclaringType;
        }

        return null;
    }
}

#endif
