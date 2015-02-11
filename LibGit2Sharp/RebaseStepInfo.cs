using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibGit2Sharp.Core;

namespace LibGit2Sharp
{
    /// <summary>
    /// Information on a particular step of a rebase operation.
    /// </summary>
    public class RebaseStepInfo
    {
        /// <summary>
        /// Needed for mocking purposes.
        /// </summary>
        protected RebaseStepInfo()
        { }

        internal RebaseStepInfo(RebaseStepOperation type, Commit commit, string exec, long stepIndex, long totalStepCount)
        {
            Type = type;
            Commit = commit;
            Exec = exec;
            CurrentStep = stepIndex;
            TotalStepCount = totalStepCount;
        }

        /// <summary>
        /// The rebase operation type.
        /// </summary>
        public virtual RebaseStepOperation Type { get; private set; }

        /// <summary>
        /// The object ID the step is operating on.
        /// </summary>
        public virtual Commit Commit { get; private set; }

        /// <summary>
        /// Command to execute, if any.
        /// </summary>
        public virtual string Exec { get; private set; }

        /// <summary>
        /// The index of this step.
        /// </summary>
        public virtual long CurrentStep { get; private set; }

        /// <summary>
        /// The total number of steps.
        /// </summary>
        public virtual long TotalStepCount { get; private set; }
    }
}
