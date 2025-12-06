/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Rapid.Pb;

namespace Rapid.Monitoring;

public interface IEdgeFailureDetectorFactory
{
    IEdgeFailureDetector CreateInstance(Endpoint subject, Action notifier);
}

public interface IEdgeFailureDetector : IDisposable
{
    void Start();
    void Stop();
}
