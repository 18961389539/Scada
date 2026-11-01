export const chartColors = ['#5b8ff9', '#61ddaa', '#f6bd16', '#7262fd', '#78d3f8', '#9661bc', '#f6903d', '#657798', '#ff9d4d', '#f759ab', '#36cfc9', '#9254de']

export function seriesColor(index: number): string {
  return chartColors[index % chartColors.length]!
}
