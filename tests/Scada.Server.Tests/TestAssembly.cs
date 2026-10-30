using Xunit;

// 这套用例里有直接起真实进程的 HA 双实例、会绑本地端口的 OPC/TCP/网关用例，以及依赖定时器与
// SQLite 文件锁的集成用例。并行跑时它们互相抢 CPU、端口和临时目录，同一条用例会在不同的机器负载下
// 随机失败（例如聚合任务在用例断言前重算、OPC 客户端读到别的实例的值）。
// 串行执行只多花一两分钟，换来的是同一份代码在任何机器上跑出同一个结果。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
