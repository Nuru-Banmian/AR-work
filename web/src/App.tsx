import { useEffect, useState } from 'react'
import { errorMessage, listIssues, serverAddress, updateStatus, type Issue } from './api'
import './App.css'

const priorityNames = { low: '低', medium: '中', high: '高' }
const statusNames = { open: '待处理', in_progress: '处理中', resolved: '已解决' }

function IssueCard({ issue, address, onSaved }: { issue: Issue; address: string; onSaved: () => void }) {
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')

  async function changeStatus(status: Issue['status']) {
    if (saving || status === issue.status) return
    setSaving(true)
    setError('')
    setMessage('')
    try {
      await updateStatus(address, issue.id, status)
      onSaved()
      setMessage('状态已保存')
    } catch (reason) {
      setError(`保存失败：${errorMessage(reason)}。可重试或刷新确认。`)
    } finally { setSaving(false) }
  }

  return <article>
    <div className="row"><span className={`priority ${issue.priority}`}>{priorityNames[issue.priority]}优先级</span><span>{statusNames[issue.status]}</span></div>
    <h2>{issue.title}</h2><p className="id">编号：{issue.id}</p>
    <p>{issue.description || '未填写描述'}</p>
    <dl><dt>标记位置（米）</dt><dd>x: {issue.position.x}, y: {issue.position.y}, z: {issue.position.z}</dd><dt>创建时间</dt><dd>{new Date(issue.createdAt).toLocaleString()}</dd><dt>更新时间</dt><dd>{new Date(issue.updatedAt).toLocaleString()}</dd></dl>
    <label>处理状态 <select aria-label={`${issue.title} 处理状态`} value={issue.status} disabled={saving} onChange={event => { void changeStatus(event.target.value as Issue['status']) }}>
      <option value="open">待处理</option><option value="in_progress">处理中</option><option value="resolved">已解决</option>
    </select></label>
    {saving && <p role="status">正在保存状态…</p>}
    {message && <p role="status">{message}</p>}
    {error && <p role="alert">{error}</p>}
  </article>
}

export default function App() {
  const [address, setAddress] = useState(() => localStorage.getItem('arwork.server') || 'http://localhost:8080')
  const [activeAddress, setActiveAddress] = useState(address)
  const [revision, setRevision] = useState(0)
  const [issues, setIssues] = useState<Issue[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [connection, setConnection] = useState('正在连接实时通知…')

  useEffect(() => {
    let events: EventSource
    try { events = new EventSource(`${serverAddress(activeAddress)}/api/events`) }
    catch { return }
    const reload = () => setRevision(value => value + 1)
    events.addEventListener('ready', () => { setConnection('实时通知已连接'); reload() })
    events.addEventListener('issues-changed', reload)
    events.onerror = () => setConnection('实时通知已断开，正在重连；可手动刷新')
    const resume = () => { if (document.visibilityState === 'visible') reload() }
    document.addEventListener('visibilitychange', resume)
    return () => { events.close(); document.removeEventListener('visibilitychange', resume) }
  }, [activeAddress])

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    let retry: ReturnType<typeof setTimeout> | undefined
    const timeout = setTimeout(() => controller.abort(), 10000)
    listIssues(activeAddress, controller.signal)
      .then(data => { if (active) { setIssues(data); setError('') } })
      .catch((reason: unknown) => {
        if (active) {
          setError(`无法加载问题：${errorMessage(reason)}。正在重试…`)
          retry = setTimeout(() => setRevision(value => value + 1), 2000)
        }
      })
      .finally(() => { clearTimeout(timeout); if (active) setLoading(false) })
    return () => { active = false; clearTimeout(timeout); clearTimeout(retry); controller.abort() }
  }, [activeAddress, revision])

  return <main>
    <header><p className="eyebrow">园区巡检管理</p><h1>现场问题</h1><p>查看巡检问题，更新处理进度。</p></header>
    <form className="connection" onSubmit={event => {
      event.preventDefault()
      try {
        const normalized = serverAddress(address)
        localStorage.setItem('arwork.server', normalized)
        setLoading(true)
        setError('')
        setActiveAddress(normalized)
        setRevision(value => value + 1)
      } catch (reason) { setError(reason instanceof Error ? reason.message : '服务地址无效') }
    }}>
      <label htmlFor="server">后端地址</label>
      <input id="server" value={address} onChange={event => setAddress(event.target.value)} required />
      <button disabled={loading}>{loading ? '加载中…' : '连接并刷新'}</button>
    </form>
    <p role="status">{connection}</p>
    {error && <section role="alert"><strong>{error}</strong><p>确认 Go 服务已启动、地址正确且允许浏览器跨域访问。</p><button onClick={() => { setLoading(true); setError(''); setRevision(value => value + 1) }} disabled={loading}>重试</button></section>}
    {loading && <p role="status">正在加载问题…</p>}
    {!loading && !error && issues.length === 0 && <p className="empty">暂无问题。请先在手机上选点并提交，再点击刷新。</p>}
    {!loading && !error && <section aria-label="问题列表" className="issues">{issues.map(issue => <IssueCard key={`${activeAddress}/${issue.id}`} issue={issue} address={activeAddress} onSaved={() => setRevision(value => value + 1)} />)}</section>}
  </main>
}
