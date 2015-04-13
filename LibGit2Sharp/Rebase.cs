using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibGit2Sharp.Core;
using LibGit2Sharp.Core.Handles;

namespace LibGit2Sharp
{
    /// <summary>
    /// The type of operation to be performed in a rebase step.
    /// </summary>
    public enum RebaseStepOperation
    {
        /// <summary>
        /// Commit is to be cherry-picked.
        /// </summary>
        Pick = 0,

        /// <summary>
        /// Cherry-pick the commit and edit the commit message.
        /// </summary>
        Reword,

        /// <summary>
        /// Cherry-pick the commit but allow user to edit changes.
        /// </summary>
        Edit,

        /// <summary>
        /// Commit is to be squashed into previous commit. The commit
        /// message will be merged with the previous message.
        /// </summary>
        Squash,

        /// <summary>
        /// Commit is to be squashed into previous commit. The commit
        /// message will be discarded.
        /// </summary>
        Fixup,

        /// <summary>
        /// No commit to cherry-pick. Run the given command and continue
        /// if successful.
        /// </summary>
        Exec
    }

    /// <summary>
    /// Encapsulates a rebase operation.
    /// </summary>
    public class Rebase
    {
        internal readonly Repository repository;

        /// <summary>
        /// Needed for mocking purposes.
        /// </summary>
        protected Rebase()
        { }

        internal Rebase(Repository repo)
        {
            this.repository = repo;
        }

        /// <summary>
        /// Start a rebase operation.
        /// </summary>
        /// <param name="branch">The branch to rebase.</param>
        /// <param name="upstream">The starting commit to rebase.</param>
        /// <param name="onto">The branch to rebase onto.</param>
        /// <param name="committer"></param>
        /// <param name="options"></param>
        /// <returns>true if completed successfully, false if conflicts encountered.</returns>
        public virtual RebaseResult Start(Branch branch, Branch upstream, Branch onto, Signature committer, RebaseOptions options)
        {
            Ensure.ArgumentNotNull(upstream, "upstream");

            options = options ?? new RebaseOptions();

            if (this.repository.Info.CurrentOperation != CurrentOperation.None)
            {
                throw new LibGit2SharpException(string.Format(
                    "A {0} operation is already in progress.", this.repository.Info.CurrentOperation));
            }

            Func<Branch, ReferenceSafeHandle> RefHandleFromBranch = (Branch b) =>
            {
                return (b == null) ?
                    null :
                    this.repository.Refs.RetrieveReferencePtr(b.CanonicalName);
            };

            Func<ReferenceSafeHandle, GitAnnotatedCommitHandle> AnnotatedCommitHandleFromRefHandle = 
                (ReferenceSafeHandle refHandle) =>
            {
                return (refHandle == null) ?
                    new GitAnnotatedCommitHandle() :
                    Proxy.git_annotated_commit_from_ref(this.repository.Handle, refHandle);
            };

            GitRebaseOptions gitRebaseOptions = new GitRebaseOptions()
            {
                version = 1,
            };

            using (ReferenceSafeHandle branchRefPtr   = RefHandleFromBranch(branch))
            using (ReferenceSafeHandle upstreamRefPtr = RefHandleFromBranch(upstream))
            using (ReferenceSafeHandle ontoRefPtr     = RefHandleFromBranch(onto))
            using (GitAnnotatedCommitHandle annotatedBranchCommitHandle      = AnnotatedCommitHandleFromRefHandle(branchRefPtr))
            using (GitAnnotatedCommitHandle upstreamRefAnnotatedCommitHandle = AnnotatedCommitHandleFromRefHandle(upstreamRefPtr))
            using (GitAnnotatedCommitHandle ontoRefAnnotatedCommitHandle     = AnnotatedCommitHandleFromRefHandle(ontoRefPtr))
            using (RebaseSafeHandle rebaseOperationHandle = Proxy.git_rebase_init(this.repository.Handle,
                                                                                  annotatedBranchCommitHandle,
                                                                                  upstreamRefAnnotatedCommitHandle,
                                                                                  ontoRefAnnotatedCommitHandle,
                                                                                  ref gitRebaseOptions))
            {
                RebaseResult rebaseResult = RebaseOperationImpl.Run(rebaseOperationHandle,
                                                                    this.repository,
                                                                    committer,
                                                                    options,
                                                                    true);
                return rebaseResult;
            }
        }

        /// <summary>
        /// Continue the current rebase.
        /// </summary>
        /// <param name="committer">The <see cref="Signature"/> of who added the change to the repository.</param>
        /// <param name="options">The <see cref="RebaseOptions"/> that specify the commit behavior.</param>
        public virtual RebaseResult Continue(Signature committer, RebaseOptions options)
        {
            Ensure.ArgumentNotNull(committer, "committer");

            options = options ?? new RebaseOptions();

            using (RebaseSafeHandle rebase = Proxy.git_rebase_open(repository.Handle))
            {
                var rebaseCommitResult = Proxy.git_rebase_commit(rebase, null, committer);

                // Report that we just completed the step
                if (options.RebaseStepCompleted != null)
                {
                    // Get information on the current step
                    long currentStepIndex = Proxy.git_rebase_operation_current(rebase);
                    long totalStepCount = Proxy.git_rebase_operation_entrycount(rebase);
                    GitRebaseOperation gitRebasestepInfo = Proxy.git_rebase_operation_byindex(rebase, currentStepIndex);

                    var stepInfo = new RebaseStepInfo(gitRebasestepInfo.type,
                                                      repository.Lookup<Commit>(new ObjectId(gitRebasestepInfo.id)),
                                                      LaxUtf8NoCleanupMarshaler.FromNative(gitRebasestepInfo.exec),
                                                      currentStepIndex,
                                                      totalStepCount);

                    if (rebaseCommitResult.WasPatchAlreadyApplied)
                    {
                        options.RebaseStepCompleted(new AfterRebaseStepInfo(stepInfo));
                    }
                    else
                    {
                        options.RebaseStepCompleted(new AfterRebaseStepInfo(stepInfo, repository.Lookup<Commit>(new ObjectId(rebaseCommitResult.CommitId))));
                    }
                }

                RebaseResult rebaseResult = RebaseOperationImpl.Run(rebase, repository, committer, options, false);
                return rebaseResult;
            }
        }

        /// <summary>
        /// Abort the rebase operation.
        /// </summary>
        public virtual void Abort()
        {
            using (RebaseSafeHandle rebase = Proxy.git_rebase_open(repository.Handle))
            {
                Proxy.git_rebase_abort(rebase);
            }
        }

        /// <summary>
        /// Skip this rebase step.
        /// </summary>
        public virtual void Skip()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// The info on the current step.
        /// </summary>
        public virtual RebaseStepInfo GetCurrentStepInfo()
        {
            if (repository.Info.CurrentOperation != LibGit2Sharp.CurrentOperation.RebaseMerge)
            {
                return null;
            }

            using (RebaseSafeHandle rebaseHandle = Proxy.git_rebase_open(repository.Handle))
            {
                long currentStepIndex = Proxy.git_rebase_operation_current(rebaseHandle);
                long totalStepCount = Proxy.git_rebase_operation_entrycount(rebaseHandle);
                GitRebaseOperation gitRebasestepInfo = Proxy.git_rebase_operation_byindex(rebaseHandle, currentStepIndex);
                var stepInfo = new RebaseStepInfo(gitRebasestepInfo.type,
                                                  repository.Lookup<Commit>(new ObjectId(gitRebasestepInfo.id)),
                                                  LaxUtf8NoCleanupMarshaler.FromNative(gitRebasestepInfo.exec),
                                                  currentStepIndex,
                                                  totalStepCount);
                return stepInfo;
            }
        }
    }
}
