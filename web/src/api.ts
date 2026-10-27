export type Issue = {
  modelUrl?: string
  snapshotUrl?: string
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
  let url: URL
  try { url = new URL(value.trim()) } catch { throw new Error('请输入有效的服务地址，如 http://电脑局域网IP:8080') }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash || url.pathname !== '/') {
    throw new Error('请输入 http://电脑局域网IP:8080 格式的服务地址')
  }
  return url.origin
}

export async function listIssues(address: string, signal: AbortSignal): Promise<Issue[]> {
  const response = await fetch(`${serverAddress(address)}/api/issues`, { signal })
  const result = await response.json()
  if (!response.ok) throw new Error(`服务请求失败（${response.status}），请检查服务后重试`)
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
  if (!response.ok) throw new Error(`状态保存失败（${response.status}），请稍后重试`)
  if (!result.data || result.data.id !== id || result.data.status !== status) throw new Error('服务返回了无效的保存结果，请刷新确认')
  return result.data
}

export function errorMessage(reason: unknown): string {
  if (reason instanceof SyntaxError) return '服务器返回数据异常，请检查服务地址后重试'
  if (reason instanceof TypeError) return '网络连接失败，请检查服务地址和网络'
  if (reason instanceof Error && ['AbortError', 'TimeoutError'].includes(reason.name)) return '请求超时，请稍后重试'
  return reason instanceof Error ? reason.message : '请求失败，请稍后重试'
}

export async function updateContent(address: string, id: string, content: Pick<Issue, 'title' | 'description' | 'priority'>): Promise<Issue> {
  const response = await fetch(`${serverAddress(address)}/api/issues/${encodeURIComponent(id)}/content`, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(content), signal: AbortSignal.timeout(10000),
  })
  const result = await response.json()
  if (!response.ok) throw new Error(`内容保存失败（${response.status}），请重试`)
  if (!result.data || result.data.id !== id) throw new Error('保存结果无效，请刷新确认')
  return result.data
}

export async function deleteIssue(address: string, id: string, title: string): Promise<void> {
  const response = await fetch(`${serverAddress(address)}/api/issues/${encodeURIComponent(id)}`, {
    method: 'DELETE', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title }), signal: AbortSignal.timeout(10000),
  })
  const result = await response.json()
  if (!response.ok) throw new Error(`删除失败（${response.status}），请刷新核对标题后重试`)
  if (result.data?.id !== id) throw new Error('删除回执无效，请刷新核对')
}
