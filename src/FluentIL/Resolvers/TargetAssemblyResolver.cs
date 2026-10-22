using Mono.Cecil;

namespace FluentIL.Resolvers
{
    // Resolves self-references without placing the operation-owned target in a borrowed cache.
    internal sealed class TargetAssemblyResolver : IAssemblyResolver
    {
        private readonly IAssemblyResolver _dependencies;

        public TargetAssemblyResolver(IAssemblyResolver dependencies)
        {
            _dependencies = dependencies;
        }

        public AssemblyDefinition Target { get; set; }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            if (Target != null && name.FullName == Target.Name.FullName)
                return Target;
            return _dependencies.Resolve(name);
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (Target != null && name.FullName == Target.Name.FullName)
                return Target;

            return _dependencies.Resolve(name, parameters);
        }

        public void Dispose()
        {
            // Release the target without disposing the caller's resolver or dependencies.
            Target = null;
        }
    }
}
