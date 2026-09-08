using System.Reflection;
using FluxMapper.Core.Ir;

namespace FluxMapper.Core.Conventions;

/// <summary>
/// Stage 1 of the plan-builder pipeline: for a given
/// destination member, gather every plausible source (exact match, naming convention, method call,
/// flattening). Every candidate is kept — the plan builder, not this class, decides which one wins and
/// whether a tie means failure.
/// </summary>
public static class CandidateDiscovery
{
    private const int MaxFlattenDepth = 4;

    /// <summary>Convenience overload keyed off a destination property/field.</summary>
    public static IReadOnlyList<CandidateSource> Discover(MemberInfo destinationMember, Type sourceType, NamingConvention naming)
        => Discover(destinationMember.Name, sourceType, naming);

    /// <summary>
    /// Core overload keyed off a plain name, so the same discovery logic serves both destination
    /// members (properties/fields) and constructor parameters (ConstructorSelector): constructor
    /// parameters are resolved by the same convention machinery as members, not a separate, weaker
    /// heuristic.
    /// </summary>
    public static IReadOnlyList<CandidateSource> Discover(string destinationName, Type sourceType, NamingConvention naming)
    {
        var results = new List<CandidateSource>();

        // 1) Exact + naming-convention match against direct members.
        foreach (var sourceMember in TypeClassification.GetReadableMembers(sourceType))
        {
            if (string.Equals(sourceMember.Name, destinationName, StringComparison.Ordinal))
            {
                results.Add(new CandidateSource(
                    new ResolvedSource.MemberChain([sourceMember]), CandidateSource.ExactMatch, sourceMember.Name));
            }
            else if (naming.NamesMatch(sourceMember.Name, destinationName))
            {
                results.Add(new CandidateSource(
                    new ResolvedSource.MemberChain([sourceMember]), CandidateSource.NamingConvention, sourceMember.Name));
            }
        }

        // 2) Method-call convention: GetName() / IsActive-style zero-arg methods.
        foreach (var method in sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetParameters().Length != 0 || method.ReturnType == typeof(void)) continue;
            if (method.DeclaringType == typeof(object)) continue;

            var candidateName = method.Name.StartsWith("Get", StringComparison.Ordinal) && method.Name.Length > 3
                ? method.Name.Substring(3)
                : method.Name;

            if (naming.NamesMatch(candidateName, destinationName))
            {
                results.Add(new CandidateSource(
                    new ResolvedSource.MethodCall(method), CandidateSource.NamingConvention, method.Name + "()"));
            }
        }

        // 3) Flattening: decompose the destination name into a chain of nested member names
        //. Only descends into
        //    Complex types, and tracks visited types on the current path to avoid infinite recursion
        //    On self-referencing graphs.
        foreach (var chain in FindFlattenChains(sourceType, destinationName, naming, depth: 0, visited: []))
        {
            if (chain.Count > 1)
            {
                results.Add(new CandidateSource(
                    new ResolvedSource.MemberChain(chain), CandidateSource.Flattening,
                    string.Join(".", chain.Select(m => m.Name))));
            }
        }

        return results;
    }

    private static IEnumerable<List<MemberInfo>> FindFlattenChains(
        Type type, string remainingName, NamingConvention naming, int depth, HashSet<Type> visited)
    {
        if (depth >= MaxFlattenDepth || remainingName.Length == 0 || !visited.Add(type))
            yield break;

        foreach (var member in TypeClassification.GetReadableMembers(type))
        {
            var normalizedMemberName = naming.Normalize(member.Name);
            if (!remainingName.StartsWith(normalizedMemberName, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = remainingName.Substring(normalizedMemberName.Length);
            var memberType = MemberValueTypeHelper.GetMemberType(member);

            if (remainder.Length == 0)
            {
                yield return [member];
            }
            else if (TypeClassification.IsComplex(memberType))
            {
                foreach (var subChain in FindFlattenChains(memberType, remainder, naming, depth + 1, visited))
                {
                    var chain = new List<MemberInfo> { member };
                    chain.AddRange(subChain);
                    yield return chain;
                }
            }
        }

        visited.Remove(type);
    }
}
