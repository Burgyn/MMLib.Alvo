import { visit } from 'unist-util-visit';

export function remarkMermaidPre() {
  return (tree) => {
    visit(tree, 'code', (node, index, parent) => {
      if (node.lang !== 'mermaid' || parent === undefined || index === undefined) return;
      parent.children[index] = {
        type: 'mermaidDiagram',
        data: { hName: 'pre', hProperties: { className: ['mermaid'] }, hChildren: [{ type: 'text', value: node.value }] },
      };
    });
  };
}
