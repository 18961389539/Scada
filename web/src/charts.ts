import { use } from 'echarts/core'
import { BarChart, GaugeChart, LineChart, PieChart } from 'echarts/charts'
import {
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  MarkAreaComponent,
  MarkLineComponent,
  TooltipComponent
} from 'echarts/components'
import { LabelLayout } from 'echarts/features'
import { CanvasRenderer, SVGRenderer } from 'echarts/renderers'

use([
  BarChart, GaugeChart, LineChart, PieChart, DataZoomComponent, GridComponent,
  LegendComponent, MarkAreaComponent, MarkLineComponent, TooltipComponent,
  LabelLayout, CanvasRenderer, SVGRenderer
])

export { init, getInstanceByDom } from 'echarts/core'
export type { ECharts } from 'echarts/core'
export type { EChartsOption } from 'echarts'