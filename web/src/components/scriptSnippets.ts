import { tr } from '../i18n'

export function scriptSnippets(): { id: string; label: string; insert: string }[] {
  return [
    { id: 'read', label: tr('designReview2.scriptSnippetRead'), insert: 'var value = context.Read("${1:tag}");\ncontext.Output("${2:name}", value);' },
    { id: 'sample', label: tr('designReview2.scriptSnippetSample'), insert: 'var sample = context.ReadSample("${1:tag}");\ncontext.Log(sample.Quality + " " + sample.Timestamp);\nif (sample.Good) context.Output("${2:name}", sample.Value);' },
    { id: 'write', label: tr('designReview2.scriptSnippetWrite'), insert: 'context.Write("${1:tag}", ${2:0});' },
    { id: 'argument', label: tr('designReview2.scriptSnippetArgument'), insert: 'if (context.HasArgument)\n{\n    var argument = context.Argument;\n    $0\n}' }
  ]
}
