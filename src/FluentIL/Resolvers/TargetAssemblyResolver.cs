using Mono.Cecil;
using System;

namespace FluentIL.Resolvers
{
    // Resolves self-references without placing the operation-owned target in a borrowed cache.
    internal sealed class TargetAssemblyResolver : IAssemblyResolver
    {
        private readonly IAssemblyResolver _dependencies;
        private AssemblyDefinition _target;
        private IDisposable _registration;

        public TargetAssemblyResolver(IAssemblyResolver dependencies)
        {
            _dependencies = dependencies;
        }

        public AssemblyDefinition Target
        {
            get => _target;
            set
            {
                _registration?.Dispose();
                _registration = null;
                _target = value;
                // Make dependency back-references find the active target too.
                if (value != null && _dependencies is CachedAssemblyResolver resolver)
                    _registration = resolver.UseTarget(value);
            }
        }

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
