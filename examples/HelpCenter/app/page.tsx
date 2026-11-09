import { definePage, type Metadata } from 'netjes';
import { pages } from '../generated/netjes.ts';
import { ArticleList } from '../src/ArticleList.tsx';

export const metadata: Metadata = { title: 'Help center' };

/** The help center's home, at the mount itself: every article, from the C# loader MapHelpCenter maps at "/". */
export default definePage(pages['/'], function HelpHome({ articles }) {
  return (
    <section>
      <h1 className="mt-0 text-help-ink">How can we help?</h1>
      <ArticleList articles={articles} />
    </section>
  );
});
