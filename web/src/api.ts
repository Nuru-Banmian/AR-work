export type Issue = {
  id: string
  title: string
  description: string
  priority: 'low' | 'medium' | 'high'
  status: 'open' | 'in_progress' | 'resolved'
  position: { x: number; y: number; z: number }
  createdAt: string
  updatedAt: string
}

export function serverAddress(value: string): string {
  const url = new URL(value.trim())
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash || url.pathname !== '/') {
    throw new Error('请输入 http://电脑局域网IP:8080 格式的服务地址')
  }
  return url.origin
}

export async function listIssues(address: string, signal: AbortSignal): Promise<Issue[]> {
  const response = await fetch(`${serverAddress(address)}/api/issues`, { signal })
  const result = await response.json()
  if (!response.ok) throw new Error(result.error?.message || `服务返回 ${response.status}`)
  if (!Array.isArray(result.data)) throw new Error('服务返回了无效的问题列表')
  return result.data
}

export async function updateStatus(address: string, id: string, status: Issue['status']): Promise<Issue> {
  const response = await fetch(`${serverAddress(address)}/api/issues/${encodeURIComponent(id)}/status`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ status }),
    signal: AbortSignal.timeout(10000),
  })
  const result = await response.json()
  if (!response.ok) throw new Error(result.error?.message || `服务返回 ${response.status}`)
  if (!result.data || result.data.id !== id || result.data.status !== status) throw new Error('服务返回了无效的保存结果，请刷新确认')
  return result.data
}
