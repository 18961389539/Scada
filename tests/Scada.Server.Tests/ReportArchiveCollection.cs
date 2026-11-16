namespace Scada.Server.Tests;

// 报表归档相关的类共用一个临时数据根下的归档目录与签名证书。
// 全量并行时曾出现「归档文件缺失」的偶发红：两个类同时生成/下载/清理报表文件，
// 文件系统操作与审计落库的时序互相穿插。这里把两个类放进同一 collection 串行，
// 并禁用与其他 collection 的并行，避免归档目录与证书在测试之间互相踩踏。
[CollectionDefinition("report-archive", DisableParallelization = true)]
public sealed class ReportArchiveCollection;