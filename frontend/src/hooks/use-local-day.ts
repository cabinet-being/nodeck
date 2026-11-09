import * as React from 'react';

function localDateKey() {
  const date = new Date();
  return `${date.getFullYear()}-${date.getMonth() + 1}-${date.getDate()}`;
}

export function useLocalDay() {
  const [day, setDay] = React.useState(localDateKey);

  React.useEffect(() => {
    let timeout: number;
    function refresh() {
      setDay(localDateKey());
      const now = new Date();
      const midnight = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
      window.clearTimeout(timeout);
      timeout = window.setTimeout(refresh, midnight.getTime() - now.getTime() + 100);
    }
    refresh();
    document.addEventListener('visibilitychange', refresh);
    return () => {
      window.clearTimeout(timeout);
      document.removeEventListener('visibilitychange', refresh);
    };
  }, []);

  return day;
}
